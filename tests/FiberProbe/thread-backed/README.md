# OS-thread-backed Wasmtime execution experiment

This branch is independent of the native callback-queue experiment. Managed
imports still use the ordinary component callback implementation. The native
Wasmtime library, rather than the callback API, is changed.

## Mechanism and scope

The pinned Wasmtime 48.0.2 patch opts into its existing Miri thread-backed
implementation via `--cfg wasmtime_thread_fibers`, without enabling `cfg(miri)`
globally. A real OS thread holds each execution stack; a mutex/condition-variable
handshake resumes and parks it. Calls can be polled from different threads, but
the execution stack and its managed callbacks stay on their original thread.
The refined patch also changes Wasmtime's runtime handshake: only an owned
`Waker` crosses from the poller, and the worker detaches/restores its Wasmtime
activation list at suspension/resumption.

This is a **feasibility experiment, not a production backend**. It intentionally
reuses the test backend's unchecked cross-thread transfers and does not claim
to have fixed its TLS or Rust soundness limitations. In particular:

- The original patch saved/restored activations on the wrong thread. The
  refined patch saves/restores them on the worker, hands parked activation
  lists back to `StoreFiber` for GC tracing, and asserts worker TLS is empty
  between activations. Focused accessor/same-Store tests are described below;
  arbitrary host TLS, broader P3 scheduling and cross-fiber trap chains still
  need validation.
  Native backtraces cannot include the polling
  caller on another thread; an upstream fiber unit test still fails.
- The test backend does not report actual stack bounds/guard ranges or support
  custom stacks. A recursive Wasm stack-exhaustion probe now traps correctly,
  but stack bounds, guard faults and custom stacks still need an audit.
- A deterministic injected worker-spawn error checks that the low-level
  constructor returns its stack and drops closure captures without running the
  body. This does not simulate actual OS thread-resource exhaustion or process
  OOM.
- If an unstarted worker's capture destructor panics during teardown, the
  worker is joined, shared state is reclaimed, and the original panic payload
  is resumed on the dropping thread. This is tested for ordinary destruction,
  not when the caller is already unwinding.
- The initial minimal build excludes Wasm GC, pooling, WASI and core stack
  switching. The later default-feature build exercises guest Wasm GC, but
  enabling other features is not evidence that their execution paths are safe.
- Arbitrary Rust `!Send` host state must not be assumed safe. This reuses
  `spawn_unchecked` and an unchecked closure transfer for the runtime's
  explicitly unsafe borrowed constructor. The current patch requires Send
  Store data at async instantiation entrypoints, including locally polled
  futures. Synchronous instantiation still accepts non-Send data. This closes
  the previously reproduced Store-data hole, not all cross-thread lifetime
  and memory-model obligations.
- There is one real thread per live execution context, not a bounded thread
  pool. Suspended stackful tasks can retain those threads.
- Selected P3 async-lifted reads and resource-destructor paths are now covered
  below; this does not establish all futures/streams or nested async paths.
  Pooling allocation, memory protection keys and Windows remain unsupported
  or unvalidated.
  macOS has a separately measured Mach-port incompatibility described below.
  The refined backend explicitly rejects active protection keys. Pooled
  custom stack allocation is unsupported and fails native unit tests.
  Engine creation now rejects pooling and configured custom stack creators
  before allocation; on-demand execution remains supported.

### P3 reads and resource-destructor lifecycle

The native patch now includes twenty isolated lifecycle regressions:

- Async-lifted guest exports synchronously read host-produced futures/streams.
  Producers first poll on workers, then on both original and migrated schedulers.
  Tests check exact ABI results, guest payloads, pending cancellation, explicit
  producer errors, and exactly-once destruction.
- Guest async reads return `BLOCKED`; synchronous cancel-read and retry run
  twice on the same handle, with exact `CANCELLED` results and two producer
  finish requests. Guest close promptly retires the producer.
- Async `future.cancel-read` and `stream.cancel-read` both return `BLOCKED`
  while producer cancellation is pending. Guest waitable-set join/wait observes
  completion before retrying the same handle; each producer receives exactly
  two finish requests and is destroyed once.
- Guest resource drops use both async and concurrent host destructors through
  a sync-lifted export called asynchronously. Destructors remain on their worker
  while polling migrates; concurrent destructor accessor TLS preserves `Taken`.
  Completion, call-future cancellation and host errors each destroy the
  destructor future once.
- Four future/stream reader cases keep a GC object live in a guest local across
  the synchronous P3 read. On migrated scheduler polls, the producer explicitly
  collects while the guest stack is parked; a stack-root count must be nonzero,
  and the guest checks the object's field after resuming. Both deferred
  reference counting and copying GC are covered.

Cases require started workers, saved activation evidence for parked operations,
empty scheduler TLS outside polls, and zero live workers after teardown. Each
runs in a 60-second-bounded subprocess, followed by a fresh healthy Store control.
The earlier combined native suite passed 31/31 on Linux Arm64 and macOS Arm64
(explicit Unix signals), and the original fourteen cases passed ten repetitions
on each platform (140/140 each). The current 34-test P3 subset passed on Linux
Arm64 and macOS Arm64; Linux passed ten serial repetitions (340/340). The
concurrent `thread_tests` module passed 10/10 on Linux Arm64 and ten serial
repetitions (100/100), including parked-root and parked-sibling trap cases.
It also passed 10/10 once under Linux x64 Docker emulation. The two new async
cancel-write cases passed ten repetitions on macOS Arm64 and under Linux x64
emulation. Stock controls passed 3/3 on macOS. No production runtime fix was
needed for these measured paths, and no managed matrix rerun is claimed.

This does not prove all P3 paths. Selected writer/consumer and multi-item
backpressure paths are now covered below; resource-bearing payloads,
guest-to-guest transfers, zero-length readiness, callback-style exports and
broader concurrent combinations remain unproven. Upstream concurrent resource
destructors currently use exclusive Store access, not genuinely parallel
destruction. Future destruction after
cancellation/error does not guarantee external resource cleanup. Selected
panic paths are now tested below; OOM and
the previously listed platform, Mach-handling and soundness limits remain open.

### P3 writer/consumer and backpressure lifecycle

Fourteen additional native regressions test pending guest future/stream writes
to host consumers. They require worker-to-scheduler migration, completion,
call-future cancellation, explicit host errors and exactly-once cleanup.
Three-item streams accept one item per operation: the guest checks exact
partial-count ABI results and retries the unwritten tail; the host verifies
`[42, 43, 44]` without duplication.

Backpressure tests take an item but hold its acknowledgement pending across
polling/migration. No next item can be taken and the guest cannot finish early.
Separate cases cancel or error after taking the first item. Guest stream
cancel-write and retry run twice on the same handle, with exact result codes,
two finish requests and no consumed payload. Future cancel-and-close is also
covered. Async guest `future.cancel-write` and `stream.cancel-write` return
`BLOCKED` while the host consumer acknowledges cancellation; waitable-set
completion is observed before cleanup. The stream retries the same handle,
while the future case closes after cancellation.

Retrying a cancelled **future write with an installed host consumer** is
rejected in v48.0.2 even when no payload was consumed. A standalone public-API
control reproduced the same error on the stock backend on macOS:
`cannot write to future after previous write succeeded or readable end dropped`.
This existing behavior is documented and regression-tested, not silently
treated as successful retry support or changed by the experiment.

Linux Arm64 and macOS Arm64 (explicit Unix signals) passed the earlier **43/43**
combined native tests and **120/120** repeated original writer cases. The
current 34-test P3 suite passed on Linux Arm64 and macOS Arm64. Linux passed ten
serial repetitions of the full P3 suite (**340/340**), including the two new
async cancel-write cases; those two cases also passed ten repetitions each on
macOS Arm64. Linux x64 under Docker emulation passed the full P3 suite 34/34,
and each new async cancel-write case passed ten repetitions. Each isolated case
includes a fresh healthy Store control. Stock configuration controls passed
3/3 on macOS. No production runtime fix or managed stress-matrix rerun is
claimed for this native-test-only follow-up.

Resource-bearing payloads, guest-to-guest transfers, zero-length readiness,
async cancel builtins beyond the selected guest cancel-read/cancel-write paths,
and callback exports remain unproven. Exactly-once
consumer destruction after cancellation/error does not guarantee external
delivery of an already-consumed item. All broader soundness/platform limits
remain open.

### Panic/unwind handoffs

Eleven native subprocess regressions cover synchronous core callback panics,
async host-future poll panics before/after suspension and migration, and
host-future cancellation Drop panics. Concurrent component callbacks panic
both on the initial worker poll and on a migrated scheduler with accessor TLS
active. Async/concurrent resource destructors panic on resumed worker polling
or cancellation Drop while the poller has migrated. A concurrent component
host future also panics after polling migrates while two same-Store sibling
guest tasks remain pending.

The original typed panic payload and origin thread must survive propagation
to `catch_unwind`; payload and future destruction are checked exactly once.
Tests require empty poller accessor/activation TLS, saved worker activations
for pending paths, zero workers after failed Store teardown, and successful
execution in a fresh healthy Store. Worker-bound destructors remain on their
original worker. Panic-hook output is intentional; subprocesses have 60-second
timeouts.

Linux Arm64 and macOS Arm64 (explicit Unix signals) each passed **53/53**
combined tests plus **100/100** repeated panic cases. Stock controls passed
3/3 on macOS. These counts cover the original ten panic cases. The new
parked-sibling case passed the full 11-case panic module and ten additional
serial repetitions on both Linux Arm64 and macOS Arm64 with explicit Unix
signals. Linux x64 under Docker emulation passed the 11-case module and ten
additional serial repetitions of the parked-sibling case. No production
runtime fix or managed matrix rerun is claimed.

These are single-panic unwind tests. Double panics during active unwinding,
`panic=abort`, panicking hooks/payload destructors, an unstarted worker capture
destructor panicking while the caller is already unwinding, broader
parked-sibling scenarios, actual OS thread-resource exhaustion or process OOM
remain unvalidated. The low-level constructor's injected spawn-error cleanup
is tested separately. Exactly-once destructor-future destruction does not
prove external resource release after a panic. No failed Store is reused;
wider soundness and platform limits still apply.

### Unsupported stack configuration boundary

The experimental native patch rejects `InstanceAllocationStrategy::Pooling`
and `Config::with_host_stack` during Engine validation, with explicit errors
pointing to on-demand allocation or removing the custom creator. This applies
to synchronous configurations too; pooling cannot be assumed safe simply
because an Engine has not yet executed an async call. Stock builds are unchanged.

Native configuration tests cover zero/one/maximum pooled-stack counts, a custom
creator that must never be invoked, and synchronous plus async on-demand core
execution. Existing component worker tests retain their parking, GC, migration,
trap and cancellation assertions. Stock controls verify Engine creation still
accepts pooling and custom creators. This rejects unsupported configurations;
it does not implement pooled stacks or fix the four earlier direct allocator
unit-test failures. Feature-reduced checks cover absence of async/pooling
(the OS-thread async backend requires `std`; no-std async remains unsupported).
The combined native configuration/worker suite passed 17/17 on Linux Arm64
and macOS Arm64 (explicit Unix signals); stock controls passed 3/3 on macOS.
The two reduced-feature checks passed.

The patch adds diagnostic native exports for threads started and still live.
Probe reports require actual backend threads for async scenarios and zero live
threads after Store teardown. The synchronous control must start no threads.
Callback/polling managed thread IDs must be disjoint for the low-level scenarios.
All existing GC pressure, pending-poll, migration, result, error and cancellation
checks remain active.
The refined reports additionally require `nativeTlsSuspensions > 0` for the
yielding workloads: a nonempty worker activation list must actually be saved.
Older experimental binaries without this new diagnostic export cannot run
the current matrix silently.

## Reproducing

Use a fresh source checkout of upstream tag `v48.0.2`, Rust 1.95 or later,
CMake, a native compiler/linker, and the .NET SDK/runtimes required by the probes.
Keep the native checkout outside this repository. The patch is intentionally
applied only once to a clean source tree.

```sh
git clone --branch v48.0.2 --depth 1 \
  https://github.com/bytecodealliance/wasmtime /tmp/wasmtime-thread-experiment
bash tests/FiberProbe/thread-backed/build.sh /tmp/wasmtime-thread-experiment default

# Linux; use a new output directory for each run.
bash tests/FiberProbe/thread-backed/run-unix.sh \
  /tmp/wasmtime-thread-experiment/target/release/libwasmtime.so \
  /tmp/thread-fibers-net10-release net10.0 Release

# macOS Arm64; explicit signal-handler experiment (default Mach mode fails).
WASMTIME_FIBER_MACOS_MACH_PORTS=0 bash tests/FiberProbe/thread-backed/run-unix.sh \
  /tmp/wasmtime-thread-experiment/target/release/libwasmtime.dylib \
  /tmp/thread-fibers-macos-net10-release net10.0 Release
```

The default build includes normal C API features, including Wasm GC, and runs
all 56 cases. To reproduce the reduced-feature experiment, use a separate fresh
native checkout with `build.sh <source> minimal`, and pass `minimal` as the fifth
argument to `run-unix.sh` (after framework and configuration). This explicitly
excludes the four guest-GC cases, leaving 52 cases. Do not run the full default
matrix with a minimal library: guest GC requires native GC support.
`run-linux.sh` remains a Linux-only compatibility wrapper for `run-unix.sh`.

The runner copies test outputs into an isolated runner directory before
replacing only its child probe library. It never replaces the library in the
source native cache or packaged runtime assets. Ordinary builds/tests continue
to use the packaged library. Setting `WASMTIME_THREAD_FIBER_EXPERIMENT=1` with a
normal library must fail due to missing diagnostic exports, not silently pass.
The probe checks those exports before starting guest execution, so a loader
fallback to a packaged library fails immediately.

Increase `WASMTIME_FIBER_ITERATIONS` for a soak. Use
`WASMTIME_FIBER_GC_STRESS=0xC` and a larger
`WASMTIME_FIBER_TIMEOUT_SECONDS` for CLR GC stress. Dumps/TRX files go into the
specified output directory and can contain process memory.

## Initial Linux Arm64 Docker results

At 20 iterations, all original nine scenarios across server/workstation GC and
background GC on/off passed:

| Runtime | Managed build | Passed | Failed |
| --- | --- | ---: | ---: |
| .NET 9.0.20 | Debug | 36 | 0 |
| .NET 9.0.20 | Release | 36 | 0 |
| .NET 10.0.12 | Debug | 36 | 0 |
| .NET 10.0.12 | Release | 36 | 0 |

The native runtime was compiled in Rust Release mode in every case; Debug/Release
above refers to the .NET library/probe. Native compilation used Rust 1.95.0 and
CMake 3.25.1, with `cranelift,wat,component-model-async` and no default features.

In the four-store concurrent cases, reports showed four native execution threads,
four managed callback thread IDs, eight polling thread IDs, and zero live native
execution threads after teardown. The callback/polling sets were disjoint.
Polling migrated; the guest/managed execution stacks did not.

A 200-iteration .NET 10 Release concurrent soak passed all four GC modes:
80,000 callbacks and forced compacting collections, 800 migrated calls, four
execution threads created, and zero execution threads left alive per mode.
That is 320,000 callbacks/collections total. A separate one-iteration concurrent
run with CLR `GCStress=0xC` also passed all four modes.

For the differential control, the **same source and same minimal features**
were compiled without `--cfg wasmtime_thread_fibers`. All four direct concurrent
managed-callback cases crashed; all four concurrent callback-free cases passed.
Thus merely reducing native features did not eliminate the original failure.
Enabling the experiment probe flag with the packaged library also failed
explicitly with `EntryPointNotFoundException`, as required.

## Additional confidence tests

Five opt-in workloads add 20 cases to the original 36:

- **Nested synchronous / asynchronous calls:** four concurrent outer Stores
  call managed imports which enter separate inner Stores. Callback-local
  managed roots survive nested execution and compacting CLR collections.
  Guest traps occur in disposable Stores; healthy independent Stores continue.
  Each case checks 8,000 nested calls/traps and 32,000 managed callbacks/forced
  collections at 20 iterations.
- **Lifecycle:** 20 cancellation/host-error/guest-trap cycles per case, with
  zero live execution threads after every cycle and successful fresh-Store
  calls after errors.
- **Guest Wasm GC:** a guest GC struct remains live across fuel yields,
  polling migration, managed callbacks and allocation pressure from 64 KiB
  GC arrays. Each managed callback explicitly invokes `Store.GC()` and also
  forces CLR collection. Each case requires 8,000 guest-GC requests, 8,000
  callbacks/CLR collections and 80 migrated calls at 20 iterations, and checks
  the guest root's field afterward.
- **Stack exhaustion:** non-tail-recursive guest code must raise
  `call stack exhausted`, not crash the process. Each case checks 20 traps
  in independent disposable Stores.

With the default-feature native library (Rust Release), the complete matrix
passed on Linux Arm64:

| Runtime | Managed build | Iterations | Passed | Failed | Duration |
| --- | --- | ---: | ---: | ---: | --- |
| .NET 9.0.20 | Debug | 20 | 56 | 0 | 4m59s |
| .NET 10.0.12 | Release | 20 | 56 | 0 | 4m50s |

Nested-async, guest-GC and stack-exhaustion cases also passed with CLR
`GCStress=0xC`: 12/12 on .NET 10 Release at one iteration (4m28s). The minimal
runner's explicit guest-GC exclusion was tested separately: 52/52 at one
iteration. The broader component suite with the modified default-feature
Linux library passed 146/146. Ordinary packaged-library component tests on
macOS Arm64 also passed 146/146, and the extended theory skips without the
experiment flag. That
macOS result does **not** validate the modified native backend on macOS.

Native fiber unit tests are **7/8 passing, not all green**. The initial
`fiber_stack_max_size` failure was fixed by rejecting sizes above `isize::MAX`
in the experimental backend. `backtrace_traces_to_host` still fails because
the real worker-thread backtrace cannot reach the polling caller's stack.
The test has not been disabled or exempted. This is a genuine semantic gap
for any production implementation.

Component traps can leave Store entry state unusable (`cannot enter component
instance`), also reproduced with the stock runtime. Recovery checks therefore
use fresh/independent Stores; they do not claim that a trapped Store is reusable.
Guest-GC tests cover the default-selected collector and a root on the active
execution stack across yields; they do not establish safety of every collector
or roots on other parked workers in the same Store.

## Rust/TLS refinement

The original Miri-based experiment invalidated two assumptions in Wasmtime's
stack-switching implementation:

1. `Context` is not Send/Sync, yet a pointer to the poller's stack-local
   `Context` was dereferenced on the worker. The refined backend passes an
   owned Send `Waker` and builds each `Context` locally while polling the host
   future. The old lifetime transmute is compiled out for this backend.
2. `AsyncWasmCallState::push/restore` operated on poller TLS while actual
   activations lived in worker TLS. Parked worker activations were therefore
   absent from `StoreFiber`'s saved list, including its GC tracing path.
   Suspension now detaches the chain on the worker and returns it with the
   yield message. Resumption, including cancellation, reattaches it on that
   same worker before execution or unwinding. Poller TLS is not changed.

Additional ownership safeguards:

- The safe low-level `Fiber::new` requires owned `'static`, Send captures and
  Send message types. Borrowed runtime execution goes through the explicitly
  unsafe `new_unchecked` constructor with documented lifetime/exclusivity
  obligations; all handshake message types must still be Send.
- The thread backend no longer uses blanket unsafe Send/Sync implementations
  for its message enum, or an unnecessary Sync assertion for closure captures.
  The unchecked Send closure wrapper remains only behind the unsafe boundary.
- Dropping an unstarted worker now joins it and destroys its captures instead
  of hanging while the worker waits only for its first resume.
- Protection-key execution is explicitly rejected rather than attempting
  poller-side mask changes which do not configure the worker.

Native tests check current-waker replacement across poller migration,
worker/poller thread separation, worker activations before/after parking, and
cancellation-time destructor execution with TLS restored. Three compile-fail
doctests reject non-Send captures, non-Send messages, and borrowed captures in
the safe constructor. The doctests require `RUSTDOCFLAGS` as well as
`RUSTFLAGS` to enable the experimental cfg:

```sh
export RUSTFLAGS="--cfg wasmtime_thread_fibers --check-cfg=cfg(wasmtime_thread_fibers)"
export RUSTDOCFLAGS="$RUSTFLAGS"
cargo test --locked --release -p wasmtime --lib --features component-model-async thread_tests
cargo test --locked --release -p wasmtime-internal-fiber --features std
```

A historical third runtime test recorded the non-Send Store
contract using a thread-affinity guard with a phantom `Rc` marker (no shared
Rc reference-count mutation). Core async instantiation accepted that Store and
the guard detected execution on the wrong thread. That test's passing result
means the incompatibility was reproduced, **not that non-Send Stores are safe**.
The follow-up below replaces that diagnostic with enforced API bounds and
positive regressions. These results describe the earlier TLS-refinement binary.

With the refined default-feature native libraries:

| Validation | Result |
| --- | --- |
| Linux Arm64, .NET 10.0.12 Release, 20 iterations | 56/56, 5m31s |
| macOS Arm64, explicit signals, .NET 10.0.12 Release, 20 iterations | 56/56, 12m38s |
| macOS explicit-signal smoke, two iterations | 56/56 |
| Final startup-marker smoke, macOS explicit signals, one iteration | 56/56 |
| Linux CLR GCStress=0xC, four workloads/four GC modes, one iteration | 16/16, 7m11s |
| Focused Rust runtime tests on macOS | 3/3 (including the incompatibility diagnostic) |
| Full Linux Rust runtime unit suite | 183 passed, 4 failed |
| macOS fiber unit suite | 9/9 (host-backtrace platform exemption still applies) |
| Linux fiber unit suite | 8 passed, 1 failed (host backtrace) |
| Ownership compile-fail doctests, Linux and macOS | 3/3 each |
| Packaged macOS component/setter regression | 148/148 |

The four Linux runtime-unit failures all require pooled custom stack
allocation (`metrics::tests::stacks`, zeroed/unzeroed stack tests, and
`unix_stack_pool::tests::test_stack_pool`). The backend still returns
`Unsupported` for custom/raw stacks; the tests remain active and failing.
This is an additional explicit incompatibility, not an exemption to make the
suite green. The source also compiles with the stock stack-switching cfg.

The refined macOS backend still reproduces the default-Mach nested-call kill
(exit 137). Neither that platform failure nor the Linux host-backtrace gap was
fixed by moving Wasmtime activations. No Miri/sanitizer proof is claimed, and
same-Store parked-root tracing was still unvalidated at that stage despite
fixing the saved-list mechanism; the later focused native tests are below.

The refined native binary SHA-256 hashes are
`6f0682fe67685f18bece08e985af98ca0cc120f9824f668b9f8698d875d1336b`
(Linux) and
`8ed5ffaeb8843ef240d022598118411ffc3b531e2f1a8e0089f3620d4fe9a841`
(macOS). Historical binaries/results below and above predate this refinement.
An explicit negative check confirms the earlier thread-backend binary is
rejected at startup for its missing TLS-suspension marker.

### Async Store-data contract follow-up

The experimental backend now enforces Send Store data for `Instance::new_async`,
core `InstancePre::instantiate_async`, and component
`InstancePre::instantiate_async`. Both Linker async instantiation routes already
required Send. Existing async function-call/resource-drop and component worker
entrypoints also require Send; the safe internal `on_fiber` helper now enforces
this explicitly.

A hidden `AsyncStoreData` bound requires Send only under the experimental cfg.
The stock backend retains acceptance of non-Send data, checked by a positive
doctest covering all three changed APIs. The shared sync/async instantiation
helper retains an explicitly unsafe borrowed path with a documented Send
obligation for async callers; synchronous callers do not acquire that bound.
This separation preserves non-Send synchronous core/component instantiation
without pretending an erased Store pointer proves transfer safety.

Three new compile-fail doctests reject non-Send Store data even when the returned
future is local and never polled. Runtime regressions verify synchronous
thread-affine Store data stays on its creator and Send data successfully uses
all five core/component instantiation routes. They replace the previous
incompatibility diagnostic.

Validation of this follow-up uses the focused Rust tests and compiler tests,
plus a rebuilt default-feature macOS C API library and the full 56-case managed
matrix at two iterations with explicit Unix signals. This is a shorter
regression run, not a repeat of the earlier 20-iteration/GCStress evidence.

| Follow-up validation | Result |
| --- | --- |
| Focused Rust runtime tests, Linux Arm64 and macOS Arm64 | 4/4 each |
| Async Store-data compile-fail doctests, both platforms | 3/3 each |
| Stock-backend non-Send async startup doctest, macOS | 1/1, covers all three changed APIs |
| Rebuilt macOS library, .NET 10 Release, explicit signals, two iterations | 56/56 |
| Eight-file patch applied to pinned original sources | Exact byte-for-byte match |

The rebuilt macOS library SHA-256 is
`768a9b1b51c5b55827aa1d1071ab489dc92116c3ba85d15342e120fb6ba81e37`.
The isolated managed runner's copy has the same hash. This follow-up did not
rebuild/repeat the Linux managed matrix or the CLR GCStress runs.

The unchecked borrowing, remaining TLS/P3 audits, pooled stacks, host backtraces,
default macOS Mach handling, Windows and native x64 hardware validation remain
open. Selected Linux x64 Rust suites passed under Docker emulation; this does
not substitute for native x64 hardware validation.

### Component/accessor TLS and same-Store parked-root follow-up

The native fork now includes six focused regressions for sync-lowered concurrent
imports with three explicit guest threads sharing one Store. Host futures must
be polled first on three distinct workers and subsequently on two scheduler
threads, retaining correct accessor Store data. The tests check recursive
access remains `Taken` and poller accessor/activation TLS is empty afterward.

At least six collections run while all three guests are parked. A test-only
counter requires at least three traced guest stack roots, and each resumed guest
must validate its live struct and report completion. Deferred reference counting
and the moving copying collector are covered; null GC is a non-collecting control.
Additional cases cover cancellation, host errors and nested internal accessor
scope restoration after unwinding. Recursive `run_concurrent` remains rejected,
even with another Store; that behavior was preserved, not bypassed.

No additional production TLS transfer was necessary for these routes: concurrent
host-future first-poll TLS scopes exit on the worker before it parks; subsequent
scheduler polls establish their own scopes. The accessor pointer is not copied
across threads.

Linux Arm64 Docker and macOS Arm64 explicit-signal native tests passed 10/10
combined regressions, plus ten serial repetitions of the six new cases on each
platform (60 executions each). Enable `gc-copying` to include its case:

```sh
cargo test --locked -p wasmtime --lib --features gc-copying thread_tests
```

The reproducible patch includes these test sources. This does not add CLR
GCStress evidence or establish all P3 future/stream/resource and trap-chain paths.

### Cross-fiber guest-trap follow-up

The native tests now selectively release a root or child guest into an
`unreachable` trap while two same-Store siblings remain parked. DRC and copying
GC cases require exact trap identity and guest backtrace attribution, one
completed host future at error delivery, destruction of all three futures on
Store teardown, and zero live execution workers afterward.

These four cases run in isolated child processes with 60-second timeouts.
Each also executes a fresh healthy Store on the same polling thread afterward.
Linux Arm64 Docker and macOS Arm64 explicit signals passed 14/14 combined native
regressions and ten repeats of the trap cases (40 isolated cases per platform).
No additional production trap/TLS change was necessary for the tested routes.
The failed Store is discarded rather than assuming trap recovery is supported.
Guest backtrace correctness does not resolve the native caller-backtrace gap.

Logs, TRX reports, binaries and earlier failing diagnostic runs are retained
outside the repository. The default-feature binary SHA-256 is
`28792ab78e6958009c33adbb773a3eb23fada1a89ae5d806c1a08f00f3d4099d`.

These results establish feasibility for the measured CLR boundary, not Rust
soundness, complete component-model semantics, or production safety. There is
no callback queue in this experiment. Native x64 and Windows variants have not
been tested.

## macOS Arm64: conditional compatibility, not default compatibility

Native validation uses macOS 27.0.1, Rust 1.95.0 and the same Wasmtime 48.0.2
patch/default C API features. The Apple linker produced a dylib that dyld
rejected with `mis-aligned LINKEDIT string pool`. Relinking the final C API
artifact with Homebrew LLD 21.1.8 and the installed macOS 26.5 SDK produced a
loadable dylib with the required diagnostic exports. This is a build-toolchain
issue, distinct from runtime compatibility. For a fresh checkout, the tested
linker selection can be reproduced with:

```sh
export SDKROOT=/Library/Developer/CommandLineTools/SDKs/MacOSX26.5.sdk
export MACOSX_DEPLOYMENT_TARGET=11.0
export WASMTIME_THREAD_FIBER_MACOS_LINKER=/opt/homebrew/opt/lld@21/bin/ld64.lld
bash tests/FiberProbe/thread-backed/build.sh /tmp/wasmtime-thread-experiment default
```

Use an SDK/linker actually installed on the machine. The build script does not
silently retry with another linker, modify the SDK, or normalize malformed
binaries.

**Default Mach-port exception handling fails on the modified backend.** An
isolated `nested-sync` workload at 20 iterations on .NET 10 Release was killed
with exit 137. Its matching macOS diagnostic reports `EXC_GUARD`,
`GUARD_TYPE_MACH_PORT`, `ILLEGAL_MOVE`; the faulting stack includes CoreCLR's
`MachMessage::ForwardNotification` / `SEHExceptionThread`. The same packaged
runtime workload passed, checking 8,000 nested calls/traps and 32,000 managed
callbacks/collections. This establishes a backend-specific compatibility
failure for that comparison, not its complete native root cause.

The full default-Mach runs were stopped after reproducing nested-call kills;
they are not passing matrices. Earlier loader/marker failures, stopped runs
and the exact isolated crash diagnostic are retained as evidence.

`WASMTIME_FIBER_MACOS_MACH_PORTS=0` explicitly selects
`Config.WithMacosMachPorts(false)` in the probe; `1` selects Mach ports, and
omission retains the native default. Reports include the requested setting
(`null` means unmodified default). Invalid values or use off macOS fail.
This required fixing the existing managed setter's P/Invoke name to the real
`wasmtime_config_macos_use_mach_ports_set` export; both boolean settings have
regression tests. No production default is changed.

With **explicit Unix-signal trap handling**, all original and confidence cases
passed across server/workstation GC and background GC on/off:

| Runtime | Managed build | Iterations | Passed | Failed | Duration |
| --- | --- | ---: | ---: | ---: | --- |
| .NET 9.0.10 | Debug | 20 | 56 | 0 | 17m31s |
| .NET 10.0.12 | Release | 20 | 56 | 0 | 18m37s |
| .NET 10.0.12 | Release smoke | 2 | 56 | 0 | 1m06s |

All 56 child reports in each matrix disclose `macosMachPorts=false`, confirm
the thread backend, and report zero live native execution threads after
teardown. Native threads must actually start for every async scenario.

Concurrent managed callbacks, nested async calls, guest GC and stack exhaustion
also passed with CLR `GCStress=0xC`: 16/16 on .NET 10 Release at one iteration
(17m39s). The longer matrices and GCStress run overlapped; their durations are
not comparative performance benchmarks.

The broader component suite using the loadable modified library passed
146/146 with default Mach handling. That suite does not exercise the failing
worker-thread nesting workload. Packaged-runtime regression tests, including
the two Mach-port setter cases, passed 148/148 after the binding correction.

Native fiber unit tests passed 8/8 on macOS, but upstream already exempts macOS Arm64 from
the caller-backtrace assertion. This does **not** resolve the Linux backtrace
failure or prove that cross-thread host backtraces are preserved.

The tested native dylib SHA-256 is
`3a25fdf475b2511ffa9a989e6d1595164167650af93541890042d52d469c0884`.
Windows and native x64 hardware validation remain outstanding. Selected Linux
x64 Rust suites passed under Docker emulation only; this does not substitute
for native x64 hardware validation. Signal-mode success is not evidence that
default Mach-port mode, other exception-handler integrations, profilers/debuggers
or the unchecked cross-thread Rust transfers are safe.
