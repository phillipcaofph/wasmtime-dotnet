# Native callback bridge

The bridge started here as a probe and now ships in the library as **host callback
isolation**. Its parts are:

- the native source, in [src/native/callback-bridge](../../../src/native/callback-bridge/README.md);
- the dispatcher, [HostCallbackDispatcher](../../../src/Components/HostCallbackDispatcher.cs),
  internal and .NET 8+ only;
- the public API, [HostCallbackIsolation](../../../src/Components/HostCallbackIsolation.cs),
  `ComponentLinker.IsolateHostCallbacks` and `ComponentLinkerInstance.DefineAsyncFunction`.

It uses an unmodified Wasmtime binary. [NativeCallbackBridge.cs](NativeCallbackBridge.cs)
is now a thin probe facade over the library. It maps environment variables onto the
library settings:

| Variable | Setting |
|---|---|
| `WASMTIME_BRIDGE_MAX_WORKERS` | `HostCallbackIsolation.MaxWorkerThreads` |
| `WASMTIME_BRIDGE_SPIN_NS` | `HostCallbackIsolation.SpinDuration` |
| `WASMTIME_BRIDGE_WAKER` | progress-signal test hook |

It also keeps the unsafe direct-GC negative control.

[ComponentCallbackHooks](../../../src/Components/ComponentCallbackHooks.cs) connects the
dispatcher to the component call paths, which can then:
- attach a recorded host exception to a failed call;
- rethrow host failures that happened after the call suspended;
- wait for a completion signal instead of re-polling every millisecond;
- reject Store use from a callback that is being served off-thread;
- proxy `Store.Fuel`, `Store.GC()` and `Store.SetEpochDeadline` to the owner thread;
- drop per-Store state when a top-level operation starts or the Store is disposed.

With isolation unused, every hook is unset and behaviour is unchanged
(`Task.Delay(1)` polling, no guard).

In-process coverage is in [HostCallbackIsolationTests](../../HostCallbackIsolationTests.cs).
These tests skip when the native library is absent.

## Design

Everything Wasmtime calls (sync callback, async callback, continuation, continuation
finalizer, registration finalizer) is C in
[bridge.c](../../../src/native/callback-bridge/bridge.c) and never enters .NET.
Managed code runs only on native-created worker threads (8 MB stacks, named
"Wasmtime host callback"). Each one calls a single `UnmanagedCallersOnly` handler in
the dispatcher.
The C code has a small portability layer: pthreads on Unix; SRWLOCK,
CONDITION_VARIABLE and `_beginthreadex` on Windows.

- **Worker pool.** One global pool, grown on demand while queued requests exceed idle
  workers. It is capped by `HostCallbackIsolation.MaxWorkerThreads` (default 64, at
  most 256).
- **Sync callbacks.** The stub enqueues a stack-local request and waits. The worker
  marshals borrowed arguments/results with the existing component marshaller.
- **Spinning.** A handoff in either direction busy-waits briefly before blocking on a
  condition variable. This applies to the stub waiting for its result and to an idle
  worker waiting for work. The budget is `HostCallbackIsolation.SpinDuration`
  (default 20 µs; 0 disables it).
  - Enqueueing skips the wake-up signal when enough workers are already spinning.
  - A spinning stub always re-locks the mutex before it returns, so the completer's
    broadcast never touches a destroyed stack condition variable.
- **Owner operations.** Wasmtime finds Wasm GC roots by walking thread-local
  activations, so Store operations must run on the thread that entered Wasm. While a
  sync stub waits, a worker can ask it to run a native function on its own (fiber)
  thread (`bridge_owner_call`). `Store.GC()`, `Store.Fuel` (get and set) and
  `Store.SetEpochDeadline` use this when called from a sync isolated callback serving
  that Store. The native functions come from a table set with `bridge_set_store_op`.
  No managed code runs on the fiber.
- **Store guard.** While a callback is served, the bridge sets the library's
  `ServingContext` (an `AsyncLocal`) to that Store's context. `Store.Context`, which
  every Store operation uses, throws `InvalidOperationException` for that Store.
  - For sync callbacks, the guard is scoped to the callback.
  - For async callbacks, it flows into the task's continuations, so direct access is
    rejected before and after `await`. Owner operations still work: before the first
    yield through the blocked stub, afterwards through the poll loop driving the call.
  - Other Stores stay usable, so nested calls work.
- **Reentrancy.** Nested guest calls (into another Store) run on the worker and may
  re-enter the bridge, each level taking another worker. If the pool is full, the
  stub fails the call with a trap instead of deadlocking.
- **Async callbacks** (`wasmtime_component_linker_instance_add_func_async`). The stub
  waits only until the worker has consumed the arguments, because Wasmtime frees them
  when the stub returns. The managed `Task` writes results into a bridge-owned staging
  buffer. The C continuation moves them into Wasmtime's result slots when polled.
  Wasmtime's result storage is never touched off-thread.
- **Cancellation.** If Wasmtime drops the future while the task is running, the
  continuation finalizer marks the request cancelled and queues a cancellation that
  signals the task's `CancellationToken`. Values produced later are deleted.
  Requests are reference counted (Wasmtime + worker).
- **Waker.** Each Store context has a semaphore that is released when one of its async
  requests settles. The library's poll loop waits on it, with a 20 ms fallback while
  requests are pending and 1 ms otherwise. When the Store enabled epoch or fuel yields
  (`SetEpochDeadlineAsyncYieldAndUpdate`, `SetFuelAsyncYieldInterval`) and no request is
  pending, the poll loop instead re-polls after one thread-pool hop. Set
  `WASMTIME_BRIDGE_WAKER=0` to compare with fixed polling.
- **Worker accounting.** A worker that has released its blocked stub counts as
  *returning* until it is idle again. Enqueuing treats it as available, so the fiber's
  next callback waits for it instead of failing with a false "pool exhausted".
  Initialization creates the first worker, ensuring finalizers have somewhere to run
  before any registration is handed to Wasmtime. A new worker that receives transient OS
  resource-exhaustion errors is retried twice with 1 ms delays. The queue mutex is released
  during each delay, allowing a finishing worker to satisfy the request instead. If all
  initialization attempts fail, defining the first isolated callback throws before a
  registration exists. Later failures trap the callback; internal work is never queued
  when no worker exists to drain it.
- **Errors.** Managed exceptions become `wasmtime_error_t` messages. The original
  exception is recorded by Store context (the first failure wins). The library attaches
  it as the `WasmtimeException.InnerException`, because `Function.CallbackErrorCause`
  is thread-static and would be on the wrong thread.
  - Causes of cancelled requests are discarded.
  - Every failed call, sync or async, consumes the recorded cause. Per-Store state is
    also dropped when a top-level component operation starts and when the Store is
    disposed, because a later Store can reuse the same context address.
  - The probe asserts that no recorded cause is ever left unreported.
- **Late async failures** (after the stub returned; see upstream gap 1):
  - **Non-bool result:** Wasmtime's `bool(false)` placeholder fails its type check and
    traps the guest.
  - **Bool result:** the placeholder would type-check, so the bridge *poisons* the slot
    with an `s32`, forcing the same trap.
  - **No result:** nothing can trap. The guest continues, and when the outer call
    returns successfully, the library throws `WasmtimeException("A host import failed
    after the call suspended: …")` with the cause attached.
- **Lifetime.** Registration finalizers queue a release job that frees the `GCHandle`.
  `Finish` waits for live registrations and async requests to drain, then joins the pool.
  Native counters (registrations, settled requests, peak workers, exhaustions, owner
  operations, live/peak async, cancellations, deleted staged values, late failures,
  threads started, poisoned results) are asserted by the tests.

## Risks proven

| Risk | Evidence |
|---|---|
| Managed code on fibers corrupts CLR GC (original problem) | Bridge off: Linux `concurrent 20` SIGSEGV. Bridge on: full 36-case matrix passes. |
| Store access from a worker thread | `guest-gc`: owner-proxied GC passes 400 GCs/iteration with a live guest root. `WASMTIME_BRIDGE_UNSAFE_DIRECT_STORE_ACCESS=1` (GC directly from the worker) corrupts the root and traps on macOS and Linux. |
| Nested guest calls and deep reentrancy | `bridge-reentrant`: 3-level recursion through the bridge, traps and a guest stack overflow inside nested calls; ≥ 4 workers used concurrently. |
| Pool exhaustion deadlock | `bridge-exhaustion` (1 worker): the nested callback gets a trap, the outer call completes and the pool keeps working. |
| Async imports without per-request threads | `bridge-async`: 32 concurrently pending async imports served by ~10 workers, with exact results. |
| Cancellation after suspension | `bridge-async-cancel`: Store disposal drops pending futures. The token fires, late values are deleted (`staged deleted == cancelled == iterations`), and no request leaks. |
| Async errors before and after suspension | `bridge-async-errors`: imports returning `s32`, nothing and `bool` each fail early and late. All six failures surface as `WasmtimeException` with the managed cause, and none succeeds silently (`late == 3`, `poisoned == 1` per iteration). |
| Exception cause transfer across threads | Sync and async errors attach the original managed exception as `InnerException` (`causesTransferred`); no cause is left pending. |
| Direct Store use from a callback | `bridge-store-guard`: direct Store use (`store.Context`, `SetWasiConfiguration`) throws inside sync callbacks and inside async callbacks before and after `await`. `Fuel`, `GC()` and `SetEpochDeadline` go through the owner thread in sync callbacks, and in async callbacks both before the first yield and after it (through the poll loop). Unrelated Stores remain usable, and the guard does not leak to the caller. |
| Windows build | `bridge.c` cross-compiles for x86_64 Windows with MinGW-w64 GCC 12 (`-std=c11 -Wall -Wextra -Werror`) and exports all 16 entry points. Not executed. |

## Upstream C API gaps found

Read from Wasmtime 48.0.2 `crates/c-api/src/component/linker.rs` (unchanged on main):

1. **No error after an async callback suspends.** `error_ret` is only read synchronously.
   The continuation returns only `bool`, so the guest cannot be trapped with the real
   error. It is mitigated as described above, but the guest sees a type-mismatch trap
   instead of the host error. For zero-result imports, the guest keeps running after
   the failure. Fixing this needs an error out-parameter on the continuation.
2. **Async arguments are freed when the callback returns.** The header says they stay
   alive for the future, but `c_args` is dropped at callback return. The bridge
   therefore consumes arguments synchronously.
3. **No waker.** The continuation cannot wake its task. The bridge's own completion
   signal replaces most of the fixed 1 ms re-polling (see Overhead).
4. **Wrong header signature.** `async.h` declares
   `wasmtime_context_epoch_deadline_async_yield_and_update` as returning
   `wasmtime_error_t*`, but the Rust implementation returns nothing. Freeing the
   garbage return value aborts in `wasmtime_store_delete`, so the binding declares it `void`.

## Execution checks

Callback and polling thread IDs must be disjoint in the explicit polling scenarios, and
every bridged callback must be accounted for as a settled bridge request.

## Run

Requires .NET 9/10, CMake and a C11 compiler, on Linux or macOS. CI also builds the
library for Windows x64 and Arm64 with MSVC (`/std:c11 /experimental:c11atomics`,
Visual Studio 2022 17.5+), but no Windows runtime coverage exists yet.
Run from the repository root, using a fresh output directory outside the repository:

```sh
bash tests/FiberProbe/native-bridge/run-unix.sh /tmp/callback-bridge-release net10.0 Release
```

An optional fourth argument selects a stock `libwasmtime.so` or `libwasmtime.dylib`
instead of the repository's default native binary. Use the same native binary for
bridge and direct-path comparisons.

To build and run in Linux Docker without modifying host build outputs:

```sh
bash tests/FiberProbe/native-bridge/run-linux.sh \
  /path/to/linux/libwasmtime.so /tmp/callback-bridge-linux net10.0 Release
```

The native library must match the selected Docker architecture (host architecture
by default, override with `WASMTIME_BRIDGE_DOCKER_PLATFORM`). The runner uses a
Rust image for its C compiler and a .NET SDK image; override these with
`WASMTIME_BRIDGE_COMPILER_IMAGE` and `WASMTIME_BRIDGE_SDK_IMAGE`. Testing .NET 9
requires an image containing that SDK/runtime. The host NuGet cache is mounted
read-only; populate required dependencies before running. For offline restoration,
set `WASMTIME_BRIDGE_RESTORE_CONFIG` to an absolute NuGet configuration path.
`WASMTIME_BRIDGE_TEST_FILTER` replaces the default VSTest filter, for example
`FullyQualifiedName~HostCallbackIsolationTests|FullyQualifiedName~ComponentAsyncYieldTests`
to run the in-process isolation tests on Linux. `WASMTIME_FIBER_ITERATIONS`,
`WASMTIME_FIBER_GC_STRESS` and `WASMTIME_FIBER_TIMEOUT_SECONDS` are passed through to the
probes. GCStress runs with server GC need a timeout well above the default 120 s.

This runs the existing 36-case fiber matrix with the same assertions and forced GC,
including concurrent calls, polling-thread migration, startup, cancellation and errors.
It also runs 13 bridge scenarios across four GC configurations (52 cases).

- General coverage: shared registration across concurrent Stores, string/list values,
  duplicate registration and lifecycle.
- Risk scenarios from the table above: `guest-gc`, nested calls/Stores,
  `bridge-reentrant`, `bridge-exhaustion`, `bridge-async`, `bridge-async-cancel`,
  `bridge-async-errors` and `bridge-store-guard`.

`bridge-bench` is a manual benchmark that is not part of the test matrix. For example:
`dotnet Wasmtime.FiberProbe.dll bridge-bench 20`, with
`WASMTIME_CALLBACK_BRIDGE_EXPERIMENT` set to `1` or `0`. The shared-registration timeout is ten minutes because
serialized server GC can be substantially slower than the other scenarios.
The isolation library is copied to the output when `NativeCallbackBridgeLibrary`
is set, or when `-p:BuildCallbackBridge=true` builds it with CMake. In-repository
tests can use the latter:

```sh
dotnet test tests/Wasmtime.Tests.csproj -p:BuildCallbackBridge=true \
  --filter "FullyQualifiedName~HostCallbackIsolationTests"
```

Without `WASMTIME_CALLBACK_BRIDGE_EXPERIMENT=1`, probe callbacks use the original direct path.
Do not enable the thread-backed experiment at the same time.

## Remaining limitations

- Sync callbacks block the polling thread for their duration (as the direct path does).
- Store access from callbacks is safe only through owner operations, and direct use is
  now rejected.
  - Only GC, fuel and the epoch deadline are proxied. Memory, resources and other
    Store APIs still need owner operations, or a context type that is only valid
    during the callback.
  - After an async callback yields, owner operations run on the thread polling the
    call, between polls. The callback's thread blocks until then, and once the call
    has completed or been cancelled they are rejected.
- Nested calls into the *same* Store are rejected (by the Store guard, or by the
  library's operation guard).
- Late async failures trap the guest with a type mismatch rather than the host error.
  A zero-result import lets the guest continue past the failure before the caller sees
  it (upstream gap 1).
- A sync isolated import costs about 1.5 µs with the default spin, against 0.5 µs on
  the direct path. Without spinning it costs about 5.5 µs.
- Core-Wasm host functions (`Linker.DefineFunction`, `Function.FromCallback`) need no
  isolation: the library only calls core functions synchronously, so Wasmtime runs them
  on the caller's stack even on async engines
  (`CoreHostFunctionsRunOnTheCallingStackOnAsynchronousEngines`). An async core-call API
  would need the same bridge.
- The library does not support component resources yet. Their destructors will need
  isolation once it does. `Store.SetEpochDeadlineCallback` is isolated
  automatically on async engines when the bridge is available (as a sync job whose result
  is the deadline delta; it cannot request a yield).
- The Linux library built by CI on Ubuntu 22.04 needs glibc 2.35 or later.
- Windows runs only in CI: `main.yml` runs the in-process isolation tests and
  `fiber-stress.yml` runs the bridge probe theories on windows-2025 (isolation on only).
  Neither has been run locally. `DOTNET_GCStress=0xC` has passed all 52 bridge cases at three
  iterations each on Linux arm64; longer soaks and x64 have not been run.

Passing this matrix is evidence for the callback isolation hypothesis, not proof that
all CLR/Wasmtime stack interactions are safe. Compare against the original direct path
using the same Wasmtime build and GC configuration.

## Library integration results

Stock Wasmtime 48.0.2, .NET 10 Release, Arm64, using the library dispatcher with a
20 µs spin:

| Check | Result |
|---|---|
| Original matrix + bridge scenarios, macOS | 88/88 |
| Original matrix + bridge scenarios, Linux (Docker) | 88/88 |
| Full unit suite with in-process isolation tests, macOS | 481 passed, 2 opt-in skips |
| Component/Store regressions + isolation tests, macOS | 104 passed, 2 opt-in skips |
| NuGet consumer from a packed `.nupkg` (`runtimes/osx-arm64/native`) | isolated sync + async calls work; missing library gives `IsSupported == false` |
| Windows x86_64 | compiles with MinGW-w64 `-Werror` and exports all 18 entry points |

### Async yields and Linux x64

| Check | Result |
|---|---|
| Original matrix + bridge scenarios, Linux x64 (Docker, emulated) | 88/88 (86/88 before the worker-accounting fix: `bridge-exhaustion` false exhaustion) |
| `ComponentAsyncYieldTests` + isolation + async + epoch tests, macOS | 30/30 |
| 5,000,000-iteration loop with a 5,000-fuel yield interval | 52 ms (12 s with 1 ms re-polling) |
| `bridge-exhaustion`, `bridge-async`, `bridge-async-errors`, `bridge-store-guard`, 500 iterations, macOS | pass |

The emulated x64 run exposed a race. A worker publishes its result before it returns to
the idle count. The fiber's next callback could see no idle worker and fail fast.
Probes now use `Store.SetFuelAsyncYieldInterval`, so the yield-aware poll loop runs
alongside pending isolated requests in every bridge scenario.

The in-process tests found a leak, fixed here. Synchronous failures were not consuming
the recorded cause. The cause could then surface from a later Store allocated at the
same context address. The tests also found that per-Store progress signals were
never released.

### Isolated epoch callbacks and GCStress

| Check | Result |
|---|---|
| Full unit suite, macOS | 527 passed, 4 opt-in skips |
| Original matrix + bridge scenarios, Linux arm64 (Docker) | 88/88 |
| Isolation, async-yield and epoch tests in-process, Linux arm64 (Docker) | 28/28 |
| Bridge scenarios with `DOTNET_GCStress=0xC`, 1 iteration, Linux arm64 Release runtime | 52/52 (10.5 min against 3 min without stress; 51/52 before the probe fix below) |
| `callbacks`, `concurrent`, `bridge-async-errors`, `bridge-exhaustion`, 300 iterations, macOS | pass |

`Store.SetEpochDeadlineCallback` now runs as a synchronous bridge job on async engines,
so the callback runs on a worker and can use `Fuel` and `SetEpochDeadline` through owner
operations. Under GCStress, `bridge-async-errors` exposed a timing assumption in the probe
rather than in the bridge. Its "late" failure used a 1 ms delay, which elapsed before the
slowed worker checked the task, so the failure was reported synchronously. That is the
correct early-failure behaviour. The probe now holds the failure until `CallAsync` returns,
which only happens once the guest has suspended.

### Default isolation and Store access before the first yield

| Check | Result |
|---|---|
| Full unit suite, macOS | 529 passed, 4 opt-in skips |
| Original matrix + bridge scenarios, Linux arm64 (Docker) | 88/88 (84/88 before the expected counts were updated for the new owner operations) |
| Isolation, async-yield, epoch, component function and linker tests in-process, Linux arm64 (Docker) | 70/70 |
| `callbacks`, `concurrent`, `bridge-async`, `bridge-async-errors`, `bridge-exhaustion`, `bridge-store-guard`, 300 iterations, macOS | pass |
| MinGW-w64 compile, `-Werror` | 18 exports |

`ComponentLinker` now isolates host callbacks by default when the engine has component-model
async enabled and the bridge is available. Plain `DefineFunction` callbacks on async engines
therefore no longer run managed code on Wasmtime fibers unless the caller sets
`IsolateHostCallbacks = false`. While an asynchronous callback runs synchronously, before it
first yields, the bridge stub is still blocked on the Store's thread. The stub now serves
owner operations during that window, so `Fuel`, `GC()` and `SetEpochDeadline` work there.
In that round they were still rejected after the first `await`; the next section lifts that.

### Store operations after an asynchronous callback yields

Between `wasmtime_call_future_poll` calls Wasmtime does not borrow the Store, and Wasmtime
48 traces GC roots on suspended component-async fibers (`trace_fiber_roots`), so the poll
loop in `CallAsync` and `InstantiateAsync` can run Store operations there. An async callback
that already yielded queues `Fuel`, `GC()` or `SetEpochDeadline` on its Store's progress
signal, wakes the poll loop and blocks until the loop has run the operation. When the poll
loop stops (completion, failure or cancellation) it fails queued operations, and later ones
are rejected by the Store guard as before.

| Check | Result |
|---|---|
| Full unit suite, macOS | 529 passed, 4 opt-in skips |
| Isolation and async-yield tests, macOS, 20 repeated runs | 20 × 25/25 |
| Original matrix + bridge scenarios, Linux arm64 and x64 (Docker; x64 emulated) | 88/88 each |
| Isolation, async, async-yield, epoch, component function and linker tests in-process, Linux arm64 and x64 | 75/75 each |
| `callbacks`, `concurrent`, `bridge-async`, `bridge-async-errors`, `bridge-async-cancel`, `bridge-exhaustion`, `bridge-store-guard`, 300 iterations, macOS | pass |

`ItProxiesStoreOperationsFromAsynchronousCallbacks` covers fuel, GC and epoch deadlines on
both sides of the first `await`. `ItRejectsStoreOperationsAfterTheCallEnds` cancels the call
while the callback is suspended and checks that its later `Fuel` read throws.

### Review fixes, core-Wasm callbacks and a longer GCStress run

| Check | Result |
|---|---|
| Full unit suite, macOS | 532 passed, 4 opt-in skips |
| Isolation tests, macOS, 10 repeated runs | 10 × 19/19 |
| Original matrix + bridge scenarios, Linux arm64 and x64 (Docker; x64 emulated) | 88/88 each |
| Isolation, async, async-yield, epoch, component function and linker tests in-process, Linux arm64 and x64 | 78/78 each |
| Bridge scenarios with `DOTNET_GCStress=0xC`, 3 iterations, 900 s probe timeout, Linux arm64 | 52/52 in 25 min (slowest: `nested-sync` with server + background GC, 4 min 10 s) |
| `callbacks`, `concurrent`, `bridge-async`, `bridge-async-errors`, `bridge-async-cancel`, `bridge-exhaustion`, `bridge-store-guard`, 300 iterations, macOS | pass |

- **Core-Wasm callbacks.** `CoreHostFunctionsRunOnTheCallingStackOnAsynchronousEngines`
  checks that a `Linker.DefineFunction` callback on an async engine runs on the calling
  thread, within 256 KB of the caller's stack. As a control, a fiber-hosted component
  callback measured about 30 MB away. Core host functions therefore stay unisolated.
- **Cancellation racing completion.** A cancel job could look up a callback's
  `CancellationTokenSource` just as `FinishAsync` disposed it. The resulting
  `ObjectDisposedException`, or an `AggregateException` thrown by a user cancellation
  registration, reached the job handler's fail-fast path. The sources are no longer
  disposed, since they own no timer or linked registration, and cancellation exceptions are
  contained. `ItToleratesCancellationRacingCallbackCompletion` races the two 100 times.
  The window is too narrow for that test to reproduce the old failure reliably.
- **Owner operations bound to their poll session.** Deferred operations were routed only by
  Store context, so a callback that outlived its call could in principle be served by a
  later call on the same Store. Each async callback now captures the poll session (signal
  and generation) that started it. `TryDefer` rejects any operation from an older session.
  Today Wasmtime 48 refuses to enter a Store again after a call is dropped mid-flight
  ("cannot enter component instance", at instantiation on macOS and at the next call on
  Linux), so this is defence in depth. `ItRejectsStoreOperationsFromACallbackThatOutlivedItsCall`
  covers both the current behaviour and the cross-call case should Wasmtime allow it.
- **GCStress.** The first 3-iteration run hit the probe's 120 s timeout in four
  server-GC + background-GC cases. `run-linux.sh` now forwards
  `WASMTIME_FIBER_TIMEOUT_SECONDS`. That run also exposed a probe assumption in
  `bridge-store-guard`: `Task.Delay(1)` can complete before it is awaited, so the callback
  never yields and its "after yield" operations correctly take the native path. The probe
  now forces the yield with `Task.Yield()`.

### ExecutionContext flow and worker-refusal diagnostics

| Check | Result |
|---|---|
| Full unit suite, macOS | 534 passed, 4 opt-in skips |
| Isolation tests, macOS, 10 repeated runs | 10 × 21/21 |
| Original matrix + bridge scenarios, Linux arm64 and x64 (Docker; x64 emulated) | 88/88 each |
| Isolation, async, async-yield, epoch, component function and linker tests in-process, Linux arm64 | 80/80 in 18 of 19 runs (see below) |
| The same in-process tests, Linux x64 | 80/80 |
| `bridge.c` cross-compiled with MinGW-w64, `-Wall -Wextra -Werror` | builds, 18 exports |
| `callbacks`, `concurrent`, `bridge-async`, `bridge-async-errors`, `bridge-async-cancel`, `bridge-exhaustion`, `bridge-store-guard`, 300 iterations, macOS | pass |

- **ExecutionContext.** Isolated callbacks run on native workers entered through
  `UnmanagedCallersOnly`, so they ran with whatever ExecutionContext the worker thread
  last had. They missed the caller's `AsyncLocal` values, `Activity.Current` and culture.
  Worse, a synchronous callback's `AsyncLocal` writes stayed on the worker and leaked into
  later, unrelated callbacks.
  - `Store.BeginComponentOperation` now captures the caller's context per Store context.
  - The dispatcher restores that context for synchronous and async-start jobs, then
    restores the worker's own context after every job.
  - Asynchronous continuations inherit the restored context as usual.
  - Writes made inside a callback no longer flow back to the caller or into later
    callbacks. A caller that suppressed flow gets an empty context.
  - `ExecutionContext.Restore` (.NET 8+) switches contexts without allocating per callback.
  - Covered by `ItFlowsTheCallersExecutionContextIntoSynchronousCallbacks` and
    `ItFlowsTheCallersExecutionContextIntoAsynchronousCallbacks`. Both failed before the
    change.
- **Worker refusals.** One Linux arm64 run failed `NestedInstancesInheritIsolation` with
  "worker pool exhausted". That test makes a single, non-nested synchronous callback with
  the default 64-worker limit, and the failure did not reproduce in 18 further runs, 6 of
  them alongside the probe matrix. Only a failed `pthread_create` fits, but the old message
  could not tell the two causes apart.
  - The native bridge now reports which limit refused the job: "a nested callback would
    deadlock (N of M workers busy)" or "the operating system refused a new worker thread
    (N workers, M busy)". A shutdown refusal is also reported separately.
  - Transient `EAGAIN` failures are retried twice with 1 ms delays. A link-time
    `pthread_create` shim proved that two failures recover on the third attempt with one
    worker and zero exhaustions. The bridge now creates that worker during initialization:
    three failures produce `Host callback isolation could not start its worker thread`
    before registration, instantiation or guest execution.
- **Precise cleanup test.** `ItReleasesPerStoreStateWhenTheStoreIsDisposed` compared the
  process-global tracked-context count before and after its work. Another test class could
  legitimately add a context in between, producing an intermittent false failure under
  load. It now records the 20 exact Store context handles it creates and verifies that
  each one was removed; the revised check passed 20 repeated runs.

### Permanent native worker regression tests

The former temporary thread-start injection checks now have permanent coverage in
[worker-start.c](../../../src/native/callback-bridge/tests/worker-start.c), built with
`-DCALLBACK_BRIDGE_BUILD_TESTS=ON` and run with CTest. The executable includes the real
bridge implementation and replaces only OS thread-start and delay calls. Production
builds have no test hooks and retain the same 18 exports.

Nine process-isolated cases cover invalid configuration, transient and permanent
startup errors, retrying initialization after failure, cleanup without a prior callback,
pool-limit refusal, transient and sustained growth failures, and reuse of an existing
worker during unlocked backoff. Tests assert exact attempts, delays, counters, cleanup,
and worker joins. Scheduling is controlled with condition variables, not timed sleeps.

| Check | Result |
|---|---|
| macOS Release CTest suite, 50 repetitions per case | 450/450 passed |
| Linux arm64 and x64, native executable (x64 emulated) | 9/9 each |
| Linux arm64, AddressSanitizer and UndefinedBehaviorSanitizer | 9/9 |
| Windows x64 test executable, Zig cross-compile, warnings as errors | passed; runtime execution awaits CI |
| Negative controls: retries disabled, eager worker removed, mutex held during backoff | corresponding tests failed; deadlock bounded by 15-second timeout |

The native workflow runs the suite on Linux x64/arm64, macOS, and Windows before
building publishable library artifacts. These small tests require no Wasmtime or .NET;
they complement, rather than replace, the managed integration and GCStress probes.

## Late-failure, waker, Store-guard and portability results

Stock Wasmtime 48.0.2, .NET 10 Release, Arm64, 20 iterations per worker:

| Platform | Original matrix + bridge scenarios | Bridge-off regressions |
|---|---:|---:|
| Linux (Docker) | 88/88 | — |
| macOS | 88/88 | 91/91 (2 skipped) |
| Windows x86_64 | compile only (MinGW-w64, `-Werror`) | — |

Bridge-off regressions: `ComponentLinkerTests`, `ComponentAsyncTests`,
`ComponentFunctionTests`, `StoreTests` and the original fiber matrix, all run with the
hooks unset.

### Overhead

Spinning, from `bridge-bench 20` on macOS Arm64. Each cell is the time per guest→host
import, measured over 1000 imports per call, with the waker on:

| Import | 0 (no spin) | 20 µs (default) | 50 µs |
|---|---:|---:|---:|
| Sync | 5.5 µs | 1.5 µs | 1.7 µs |
| Async, already completed | 15 µs | 10 µs | 10.7 µs |
| Async, `await Task.Yield()` | 15–18 µs | 10.5 µs | 11 µs |

Before spinning was added, the same benchmark measured:

| Import | Direct (fiber) | Bridge, waker | Bridge, fixed 1 ms polling |
|---|---:|---:|---:|
| Sync | 0.5 µs | 5.1 µs | 4.9 µs |
| Async, already completed | — | 14 µs | 1025 µs |
| Async, `await Task.Yield()` | — | 20 µs | 717 µs |

The waker makes suspended async imports about 35–70× faster. With the waker,
`bridge-async 3` (32 Stores × 3) went from 341 ms to 251 ms. Its runtime is dominated
by the callbacks' own `Task.Delay`.

## Worker-pool and async results

Stock Wasmtime 48.0.2, .NET 10 Release, Arm64, 20 iterations per worker:

| Platform | Original matrix | Bridge scenarios | Direct-path regressions |
|---|---:|---:|---:|
| Linux (Docker) | 36/36 | 48/48 | — |
| macOS | 36/36 | 48/48 | 45/45 |

Direct-path regressions: `ComponentLinkerTests`, `ComponentAsyncTests` and
`ComponentFunctionTests` with the bridge disabled. The unsafe direct-GC control
trapped on both platforms; the owner-proxied run passed.

## First prototype results (2026-10-07, thread-per-registration)

Using stock Wasmtime 48.0.2, .NET 10 Release, Arm64, 20 iterations per worker:

| Platform | Original matrix | Additional confidence cases | Direct-path regressions |
|---|---:|---:|---:|
| Linux (Docker) | 36/36 | 16/16 | 17/17 |
| macOS | 36/36 | 16/16 | 17/17 |

The Linux Docker runner completed all 52 bridge cases in one run. The macOS
confidence cases were rerun after correcting the value fixture/assertion and
allowing longer for serialized server GC; one prior shared-registration case
exceeded the original two-minute timeout under concurrent test load.
The original matrix's GC, migration and result assertions were not relaxed.
Direct-path regressions cover `ComponentLinkerTests` and `ComponentAsyncTests`
with the bridge disabled.

On Linux, disabling the bridge and running `concurrent 20` against the same
native binary reproduced SIGSEGV (exit 139). Enabling it passed the corresponding
four GC configurations, including exact callback/collection/migration counts.
These results support further development, not production readiness. Windows,
x64, .NET 9, and extended `DOTNET_GCStress` runs were not validated here.

## Wasmtime 49 compatibility

The bridge is compatible with the official Wasmtime **v49.0.2** C API artifacts,
the latest stable release as of 2026-10-08. This release includes Wasmtime's fix
for async component callback result-count validation
([GHSA-32h6-97mm-8q3c](https://github.com/bytecodealliance/wasmtime/security/advisories/GHSA-32h6-97mm-8q3c)).
The bridge's staged async-result path and error cases are included in the
validation below.

| Check | Result |
|---|---|
| Full unit suite, official macOS Arm64 C API | 534 passed, 4 opt-in skips |
| Original matrix + bridge scenarios, official Linux Arm64 C API | 88/88 |
| Original matrix + bridge scenarios, official Linux x64 C API (emulated) | 88/88 |
| Library and test builds | 0 errors; only expected SourceLink warnings in Docker |

The required component and Store-context symbols are present in the v49 shared
libraries. Comparing the v48.0.2 and v49.0.2 C headers found no removals: v49
adds component extern/implements and configuration APIs.

[`stock-wasmtime.yml`](../../../.github/workflows/stock-wasmtime.yml) makes this
compatibility durable. It downloads checksum-pinned official v49.0.2 C API
artifacts and runs the component, async-yield, epoch, linker, and host callback
isolation tests on Linux x64/Arm64, macOS x64/Arm64, and Windows x64. This is
separate from the custom thread-backed native build, so both stock Wasmtime and
the experimental native artifacts remain covered. Windows and macOS x64 runtime
results depend on that CI job; their official archive names and layouts were
verified locally.

The v48 results above remain historical evidence and are not relabeled as v49
coverage.
