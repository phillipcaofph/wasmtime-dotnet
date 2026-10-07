# Native callback bridge prototype

This opt-in experiment lives beside the [thread-backed experiment](../thread-backed/README.md),
not in the shipped Wasmtime package. It uses an unmodified Wasmtime binary.
The only library hook is internal access to the existing component callback marshaller
through the probe's existing `InternalsVisibleTo` access.

## Execution and lifetime

The actual Wasmtime callback and registration finalizer are C functions. The callback
queues a request and waits with pthread synchronization; it never enters .NET.
A dedicated managed dispatcher thread per registration reads and writes the borrowed
component-value buffers using the existing marshaller, then signals completion.
Wasmtime cannot return from the callback until this finishes, so the buffers remain
alive. Native requests are stack-local to the blocked callback.

The registration finalizer closes the queue after Wasmtime releases its callback.
After Stores and linkers are disposed, the probe joins dispatchers and destroys queues.
Native counters verify requests were handled and all queues were released.
Callback and polling thread IDs must be disjoint in the explicit polling scenarios.

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
It also runs 16 opt-in confidence cases: concurrent Stores sharing a registration,
empty/nonempty UTF-8 strings and byte lists, rejected duplicate registration, and
repeated cancellation/error/disposal cycles, each across four GC configurations.
The shared-registration test performs 8,000 forced compacting collections through
one dispatcher at the default iteration count. Its timeout is ten minutes because
serialized server GC can be substantially slower than the other scenarios.
The companion library is copied only when `NativeCallbackBridgeLibrary` is supplied.
Without `WASMTIME_CALLBACK_BRIDGE_EXPERIMENT=1`, callbacks use the original direct path.
Do not enable the thread-backed experiment at the same time.

## Deliberate limitations

- Synchronous component value callbacks only; the callback's native wait blocks the
  polling thread. This does not implement asynchronous host-function continuations.
- No Store access, guest-memory access, or nested guest calls from callbacks. The
  controlled probe callbacks do not perform these operations; this is not a general
  API enforcing safety for arbitrary user callbacks.
- Native errors and their messages are propagated, but the original managed exception
  is not transferred to the polling thread's `Function.CallbackErrorCause`. Its
  dispatcher-local copy is cleared to avoid retaining it.
- One dispatcher per registration, unbounded across registrations. Concurrent calls
  sharing a registration are serialized at its dispatcher.
- Resource destructors, core-Wasm callbacks, other callback kinds, and Windows are not
  bridged. No production safety claim is made.

Passing this matrix is evidence for the callback isolation hypothesis, not proof that
all CLR/Wasmtime stack interactions are safe. Compare against the original direct path
using the same Wasmtime build and GC configuration.

## Prototype results (2026-10-07)

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
