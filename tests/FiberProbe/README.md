# Component fiber compatibility probes

Crash-isolated stress probes for asynchronous component calls. Each xUnit case in
[ComponentFiberStressTests](../ComponentFiberStressTests.cs) launches this program in a
separate process, so a crash, wrong result, missing coverage or timeout fails the case
without taking down the test runner. Every scenario forces garbage collections while
calls are suspended, migrates polling between threads and checks that managed objects
survive.

The probes run in two modes:

- **Direct**: managed host callbacks run on Wasmtime fiber stacks. This is the hazard
  that host callback isolation exists to avoid; on Linux it reproducibly crashes in the
  CLR's stack-root scanning. Opt in with `WASMTIME_FIBER_PROBES=1` (Linux and macOS
  only; Windows fibers are unsupported by .NET).
- **Isolated**: callbacks go through host callback isolation
  ([src/native/callback-bridge](../../src/native/callback-bridge/README.md)). Opt in with
  `WASMTIME_CALLBACK_BRIDGE_EXPERIMENT=1` and build the bridge with
  `-p:BuildCallbackBridge=true`. This also enables the bridge-specific scenarios below.

## Running

```sh
WASMTIME_FIBER_PROBES=1 WASMTIME_CALLBACK_BRIDGE_EXPERIMENT=1 \
  dotnet test tests/Wasmtime.Tests.csproj -c Release -p:BuildCallbackBridge=true \
  --filter FullyQualifiedName~ComponentFiberStressTests
```

| Variable | Effect |
|---|---|
| `WASMTIME_FIBER_ITERATIONS` | Calls per worker (default 20); raise for a soak run. |
| `WASMTIME_FIBER_TIMEOUT_SECONDS` | Per-process timeout (default 120). |
| `WASMTIME_FIBER_GC_STRESS` | `DOTNET_GCStress` for the child processes only, for example `0xC`. |
| `WASMTIME_FIBER_DUMP_DIRECTORY` | Where crashed children write dumps (default: a temp directory). |
| `WASMTIME_BRIDGE_MAX_WORKERS` | `HostCallbackIsolation.MaxWorkerThreads` for the child. |
| `WASMTIME_BRIDGE_SPIN_NS` | Spin budget in nanoseconds for the child. |

The [fiber workflow](../../.github/workflows/fiber-stress.yml) runs the isolated probes on
pull requests across Linux, macOS and Windows, x64 and Arm64, .NET 9 and 10. Manual runs
add the direct-path matrix and accept soak and GC-stress settings, and CI collects the
reports and any dumps.

For diagnosis, run the program directly, for example
`dotnet tests/bin/Release/net9.0/FiberProbe/Wasmtime.FiberProbe.dll callbacks 1`.
`WASMTIME_FIBER_DIAGNOSTIC_AFFINITY=1` polls each future on a single thread and
`WASMTIME_FIBER_DIAGNOSTIC_NO_FORCED_GC=1` omits the forced collections; the xUnit
launcher clears both so they cannot weaken the tests.

## Scenarios

Both modes: callback-free and synchronous controls, synchronous and concurrent managed
callbacks under GC pressure, the public `CallAsync` path, async instantiation with a start
function calling imports, cancellation after suspension, host exceptions and guest traps.

Isolated mode adds:

| Scenario | Checks |
|---|---|
| `shared-linker` | One linker's isolated functions shared by concurrent Stores. |
| `bridge-values` | String and list values cross the bridge intact. |
| `bridge-registration` | Duplicate registrations fail and leak nothing. |
| `lifecycle` | Repeated cancellation and failure, with causes reported exactly once. |
| `nested-sync`, `nested-async` | Guest calls nested inside host callbacks. |
| `guest-gc` | Store GC from a callback runs on the owning Wasmtime activation, with a live guest root. |
| `bridge-reentrant` | Three levels of re-entry, traps and a guest stack overflow inside nested calls. |
| `bridge-exhaustion` | With one worker, a nested call traps instead of deadlocking and the pool recovers. |
| `bridge-async` | 32 pending async imports served by fewer workers than imports. |
| `bridge-async-cancel` | Disposal cancels pending imports and deletes late results. |
| `bridge-async-errors` | Early and late async failures surface with their cause; none succeeds silently. |
| `bridge-store-guard` | Direct Store use throws; `Fuel`, `GC()` and `SetEpochDeadline` work before and after a yield. |

`bridge-bench` is a manual benchmark of the time per guest-to-host import.

## Results

With stock Wasmtime and .NET 10 Release on Arm64, the isolated mode passed all 88 cases
(36 shared and 52 bridge scenarios) on Linux and macOS, while the direct mode crashes on
Linux. On macOS Arm64, an isolated synchronous import costs about 1.5 µs with the default
20 µs spin (0.5 µs direct), and an isolated async import about 10 µs.

## Wasmtime C API limitations found

- An async host function cannot report an error after it suspends: the continuation only
  returns `bool`. The binding makes the guest trap where it can and throws the failure
  from the pending `CallAsync`.
- Async host function arguments are freed when the callback returns, so the bridge copies
  them before returning.
- The continuation has no waker, so the binding signals completion itself instead of
  re-polling every millisecond.
- `async.h` declares `wasmtime_context_epoch_deadline_async_yield_and_update` as returning
  `wasmtime_error_t*`, but it returns nothing; the binding declares it `void`.
