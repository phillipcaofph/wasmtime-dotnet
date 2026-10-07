# Native callback bridge prototype

This opt-in experiment lives beside the [thread-backed experiment](../thread-backed/README.md),
not in the shipped Wasmtime package. It uses an unmodified Wasmtime binary.
The only library change makes `ComponentLinkerInstance.Invoke` internal so the probe
can reuse the existing component callback marshaller through `InternalsVisibleTo`.

## Design

Everything Wasmtime calls (sync callback, async callback, continuation, continuation
finalizer, registration finalizer) is C in [bridge.c](bridge.c) and never enters .NET.
Managed code runs only on native-created pthread workers (8 MB stacks) that call one
`UnmanagedCallersOnly` handler in [NativeCallbackBridge.cs](NativeCallbackBridge.cs).

- **Worker pool.** One global pool, grown on demand while queued requests exceed idle
  workers, capped by `WASMTIME_BRIDGE_MAX_WORKERS` (default 64). It replaces the earlier
  thread-per-registration design.
- **Sync callbacks.** The stub enqueues a stack-local request and waits. The worker
  marshals borrowed arguments/results with the existing component marshaller.
- **Owner operations.** Wasmtime finds Wasm GC roots by walking thread-local
  activations, so Store operations must run on the thread that entered Wasm. While a
  sync stub waits, a worker can ask it to run a native function on its own (fiber)
  thread (`bridge_owner_call`). `NativeCallbackBridge.CollectGuest` uses this for
  `wasmtime_context_gc`. No managed code runs on the fiber.
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
- **Errors.** Managed exceptions become `wasmtime_error_t` messages. The original
  exception is stored by Store context and attached by `TakeCause`, because
  `Function.CallbackErrorCause` is thread-static and would be on the wrong thread.
- **Lifetime.** Registration finalizers queue a release job that frees the `GCHandle`.
  `Finish` waits for live registrations and async requests to drain, then joins the pool.
  Native counters (registrations, settled requests, peak workers, exhaustions, owner
  operations, live/peak async, cancellations, deleted staged values, unreportable
  failures, threads started) are asserted by the tests.

## Risks proven

| Risk | Evidence |
|---|---|
| Managed code on fibers corrupts CLR GC (original problem) | Bridge off: Linux `concurrent 20` SIGSEGV. Bridge on: full 36-case matrix passes. |
| Store access from a worker thread | `guest-gc`: owner-proxied GC passes 400 GCs/iteration with a live guest root. `WASMTIME_BRIDGE_UNSAFE_DIRECT_STORE_ACCESS=1` (GC directly from the worker) corrupts the root and traps on macOS and Linux. |
| Nested guest calls and deep reentrancy | `bridge-reentrant`: 3-level recursion through the bridge, traps and a guest stack overflow inside nested calls; ≥ 4 workers used concurrently. |
| Pool exhaustion deadlock | `bridge-exhaustion` (1 worker): the nested callback gets a trap, the outer call completes and the pool keeps working. |
| Async imports without per-request threads | `bridge-async`: 32 concurrently pending async imports served by ~10 workers, with exact results. |
| Cancellation after suspension | `bridge-async-cancel`: Store disposal drops pending futures. The token fires, late values are deleted (`staged deleted == cancelled == iterations`), and no request leaks. |
| Async errors before and after suspension | `bridge-async-errors`: both surface as `WasmtimeException`, with the managed exception attached as the inner cause. |
| Exception cause transfer across threads | Sync and async errors attach the original managed exception (`causesTransferred`). |

## Upstream C API gaps found

Read from Wasmtime 48.0.2 `crates/c-api/src/component/linker.rs` (unchanged on main):

1. **No error after an async callback suspends.** `error_ret` is only read synchronously.
   The continuation returns only `bool`. The bridge completes with placeholder values,
   so Wasmtime reports `type mismatch: expected s32, found bool`. The managed cause is
   still attached. A function with no results, or with `bool` results, would *silently
   succeed*. Needs an error out-parameter on the continuation.
2. **Async arguments are freed when the callback returns.** The header says they stay
   alive for the future, but `c_args` is dropped at callback return. The bridge
   therefore consumes arguments synchronously.
3. **No waker.** The continuation cannot wake its task. Progress relies on the library
   re-polling with `Task.Delay(1)`.

## Execution checks

Callback and polling thread IDs must be disjoint in the explicit polling scenarios, and
every bridged callback must be accounted for as a settled bridge request.

## Run

Requires .NET 9/10, CMake, a C11 compiler, and pthreads (Linux or macOS).
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

This runs the existing 36-case fiber matrix with the same assertions and forced GC,
including concurrent calls, polling-thread migration, startup, cancellation and errors.
It also runs 12 bridge scenarios across four GC configurations (48 cases): shared
registration across concurrent Stores, string/list values, duplicate registration,
lifecycle, plus the risk scenarios above (`guest-gc`, nested calls/Stores,
`bridge-reentrant`, `bridge-exhaustion`, `bridge-async`, `bridge-async-cancel`,
`bridge-async-errors`). The shared-registration timeout is ten minutes because
serialized server GC can be substantially slower than the other scenarios.
The companion library is copied only when `NativeCallbackBridgeLibrary` is supplied.
Without `WASMTIME_CALLBACK_BRIDGE_EXPERIMENT=1`, callbacks use the original direct path.
Do not enable the thread-backed experiment at the same time.

## Remaining limitations

- Sync callbacks block the polling thread for their duration (as the direct path does).
- Store access from callbacks is safe only through owner operations. Only guest GC is
  proxied here. A product API would need proxies for memory, resources and
  fuel, and must reject direct Store use on workers. Async callbacks have no owner
  thread after suspension, so they get no Store access.
- Nested calls into the *same* Store are rejected by the library's operation guard.
- Late async failures depend on result type checking (upstream gap 1).
- Resource destructors, core-Wasm callbacks and other callback kinds are not bridged.
- Windows, x64 and extended `DOTNET_GCStress` runs are not validated. No production
  safety claim is made.

Passing this matrix is evidence for the callback isolation hypothesis, not proof that
all CLR/Wasmtime stack interactions are safe. Compare against the original direct path
using the same Wasmtime build and GC configuration.

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
