# Host callback isolation library

`wasmtime_callback_bridge` is a small C11 library that gives Wasmtime C callbacks which
never enter .NET. Asynchronous component calls run guest code on Wasmtime fiber stacks,
which the CLR cannot scan safely; the bridge's callback stubs run there instead and hand
each call to a pool of native worker threads, which enter managed code through a single
`UnmanagedCallersOnly` handler.

[bridge.c](bridge.c) is the whole implementation. It uses pthreads on Unix and Win32
primitives on Windows.

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
  isolation library.

The [callback-bridge workflow](../../../.github/workflows/callback-bridge.yml) builds the
library for linux-x64/arm64 (Ubuntu 22.04, glibc 2.35+), osx-x64/arm64 and
win-x64/arm64. The publish workflow packs those artifacts.

## Native regression tests

```sh
cmake -S src/native/callback-bridge -B build-tests -DCMAKE_BUILD_TYPE=Release \
  -DCALLBACK_BRIDGE_BUILD_TESTS=ON
cmake --build build-tests --config Release
ctest --test-dir build-tests -C Release --output-on-failure
```

These tests require only a C11 compiler and CMake, not Wasmtime or .NET. The test
executable compiles the real bridge with thread-creation and retry-delay calls
substituted at compile time. No fault-injection API or environment switch is added to
the production library, and tests are disabled by default.

Each scenario runs in a fresh process with a 15-second deadlock timeout. Tests check
exact attempt and delay counts, failed-initialization recovery, eager-worker finalizer
cleanup, worker-limit enforcement, pool growth failures, and reuse of a worker that
finishes during backoff. Condition-variable handshakes control scheduling instead of
timing-dependent sleeps. They also verify that backoff releases the queue mutex and
that shutdown drains registrations and joins every worker.

The native workflow runs these tests on Linux x64/arm64, macOS, and Windows before
building publishable artifacts.

## Worker startup

Initialization creates one ready worker; further workers are created on demand, up to the
configured maximum. This ensures registration finalizers always have a worker available
and moves an initial OS thread-creation failure into managed initialization, before
Wasmtime owns any registration.

Creating a thread can fail transiently when the operating system reports resource
exhaustion. The bridge retries `EAGAIN` twice with 1 ms delays before rejecting the
operation. It releases the queue mutex during each delay so a finishing worker can become
available instead. Other errors fail immediately. Internal work is never queued when no
worker exists to drain it.

A callback that would need more workers than the maximum fails with a Wasmtime error
instead of waiting, because a nested callback waiting for a worker could deadlock.

## ABI

The ABI is internal to this repository and may change with any library version.
