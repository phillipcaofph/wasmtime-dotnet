# Host callback isolation library

`wasmtime_callback_bridge` is a small C11 library behind `HostCallbackIsolation`. It
gives Wasmtime C callbacks that never enter .NET. Managed host callbacks run only on
native worker threads, never on Wasmtime fiber stacks, which the CLR cannot scan
safely.

- [bridge.c](bridge.c) is the whole implementation. It uses pthreads on Unix and Win32
  primitives on Windows.
- The managed side is
  [HostCallbackDispatcher](../../Components/HostCallbackDispatcher.cs). Design notes,
  risk evidence and benchmarks are in the
  [probe README](../../../tests/FiberProbe/native-bridge/README.md).

## Building

```sh
cmake -S src/native/callback-bridge -B build -DCMAKE_BUILD_TYPE=Release
cmake --build build --config Release
```

The library is written to `build/out/`, with no per-configuration subdirectory:
- `libwasmtime_callback_bridge.so` on Linux;
- `libwasmtime_callback_bridge.dylib` on macOS;
- `wasmtime_callback_bridge.dll` on Windows.

Warnings are errors. MSVC needs Visual Studio 2022 17.5 or later for C11 atomics.

From MSBuild:

- `-p:BuildCallbackBridge=true` builds it for the host. The build uses a separate
  CMake directory per target framework, then copies the library next to the
  assembly. Projects that reference `Wasmtime.csproj` get it too.
- `-p:NativeCallbackBridgeLibrary=<path>` copies a prebuilt library instead.
- When packing (`-p:Packing=true`), `-p:CallbackBridgeNativeDirectory=<dir>` adds
  `<dir>/<rid>/<library>` for every supported RID under `runtimes/<rid>/native/`.
  Packing fails if any RID is missing. Without the property the package has no
  isolation library, and `HostCallbackIsolation.IsSupported` is false.

The [callback-bridge workflow](../../../.github/workflows/callback-bridge.yml) builds the
library for linux-x64/arm64 (Ubuntu 22.04, glibc 2.35+), osx-x64/arm64 and
win-x64/arm64. The experimental publish workflow packs those artifacts.

## Worker startup

Initialization creates one ready worker; further workers are created on demand, up to
`HostCallbackIsolation.MaxWorkerThreads`. This ensures registration finalizers always
have a worker available and moves an initial OS thread-creation failure into managed
initialization, before Wasmtime owns any registration.

Creating a thread can fail transiently when the operating system reports resource
exhaustion. The bridge retries `EAGAIN` twice with 1 ms delays before rejecting the
operation. It releases the queue mutex during each delay so a finishing worker can become
available instead. Other errors fail immediately. Internal work is never queued when no
worker exists to drain it.

## ABI

The ABI is internal to this repository and may change with any library version.
The managed side checks for `bridge_set_store_op` before enabling isolation.
