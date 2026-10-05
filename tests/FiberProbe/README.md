# Component fiber compatibility probes

These are crash-isolated experiments, not a guarantee that CLR/Wasmtime stack
switching is supported. Every xUnit case launches a separate native-library
hosting process. A crash, incorrect result, missing coverage, or timeout fails
the case without crashing the xUnit runner.
The automated cases explicitly skip Windows, where native fibers are documented
as unsupported by .NET. A skip is not a successful compatibility result.

## Coverage

The matrix requests workstation/server GC and background GC on/off for:

- Native-only guest execution (no managed import invoked).
- Synchronous managed imports executing on Wasmtime's async worker stack, with
  allocation pressure, forced compacting GC, weak references, finalizable
  callback-local objects, and payload checks.
- Four concurrent workers using separate stores.
- Concurrent callback-free execution and synchronous managed-callback controls,
  to distinguish ordinary concurrency/GC from reverse P/Invoke on a fiber.
- The public `CallAsync` continuation path, with managed objects live across
  awaits and collections while guest execution is suspended.
- Async instantiation with a guest start function calling managed imports.
- Cancellation after suspension, store overlap/disposal rejection, and execution
  in a fresh store after cancellation.
- Managed host exceptions and guest traps.

Fuel-based yielding must produce incomplete native polls (or an incomplete
public task) for the yielding cases to pass. The low-level probe alternates
polls between two dedicated threads and requires both to participate. All
native input/output addresses remain stable until the future is deleted.
The guest uses synchronous canonical exports with async host execution; this
does not cover async-lifted exports, futures/streams, or async host continuations.

Reports contain runtime, OS, architecture, observed server GC mode, requested
background GC mode, GC-stress setting, callbacks, collections, pending polls,
and migrated calls. Server GC may fall back to workstation GC on a single-CPU
environment; inspect the reported mode rather than assuming it was honored.

## Running

```sh
dotnet test tests/Wasmtime.Tests.csproj \
  --filter FullyQualifiedName~ComponentFiberStressTests --logger trx

dotnet test tests/Wasmtime.Tests.csproj -c Release -p:TestTargetFramework=net10.0 \
  --filter FullyQualifiedName~ComponentFiberStressTests --logger trx
```

The default is 20 calls per worker (100 callbacks per call). Increase
`WASMTIME_FIBER_ITERATIONS` for a soak run and
`WASMTIME_FIBER_TIMEOUT_SECONDS` for its per-process timeout. No random seed is
used: workload and alternating-thread order are fixed.

For a slower GC-stress run, set `WASMTIME_FIBER_GC_STRESS=0xC`. This enables
`DOTNET_GCStress` only in child probes, not the test runner. Start with
`WASMTIME_FIBER_ITERATIONS=1` and a larger timeout.

The [fiber workflow](../../.github/workflows/fiber-stress.yml) runs Debug/Release,
.NET 9/10, and Linux/macOS x64/Arm64. Manual dispatch accepts soak/stress settings.
Child-process dumps are requested in the OS temporary directory under
`wasmtime-fiber-dumps`; CI collects them with the TRX reports. Dumps may contain
process memory and should be handled accordingly.

## Debugger validation

Build the probe, then launch its executable/DLL under a debugger with arguments
`callbacks 1` or `public-api 1`. Break in `CheckRoots`, inspect locals and managed
frames, and step through the callback. Repeat on each platform/configuration.
Debugger/profiler compatibility and upstream confirmation of CLR stack-walking
and GC invariants remain manual checks, not claims made by the automated suite.

## Failure diagnosis

Linux Arm64 Docker runs reproduced SIGSEGV/SIGABRT and timeouts. A symbolized
.NET 10.0.12 dump shows `GCInterface_Collect`, `GCToEEInterface::GcScanRoots`,
`ScanStackRoots`, `Thread::StackWalkFrames`, GC slot enumeration, and
`GCHeap::Promote` leading to an invalid object/method-table access. This implicates
managed stack-root scanning, but is not a complete proof of the original
corruption mechanism.

A binding lifetime bug was also found: the async C API borrows the function
struct for the entire future lifetime. `CallAsync` now copies it to stable
unmanaged storage, and deletes the completed future before releasing result
values. The low-level probe already used stable storage and still crashed,
so this fix alone cannot resolve all the Linux failures.

For differential diagnosis, the probe executable (not the xUnit suite) accepts:

- `sync-control 20`: four stores, synchronous calls, full callback/GC pressure.
- `concurrent-baseline 20`: four stores, real yielding/migration, no managed imports invoked.
- `WASMTIME_FIBER_DIAGNOSTIC_AFFINITY=1`: poll each future on just one worker.
- `WASMTIME_FIBER_DIAGNOSTIC_NO_FORCED_GC=1`: retain allocations and object checks,
  but omit explicit collections from callbacks and between native polls.

Reports disclose both diagnostic flags. The xUnit launcher removes these flags
so they cannot weaken the compatibility tests. Thread affinity and omitting
forced GC both still reproduced a concurrent Linux Arm64 crash in one-iteration
.NET 10 Release experiments. They are diagnostic controls, not safe workarounds.

After the lifetime fixes, the nine-scenario Linux Arm64 Docker matrix (20
iterations, server/workstation GC, background GC on/off, 30-second child timeout)
still failed:

| Runtime | Build | Passed | Failed |
| --- | --- | ---: | ---: |
| .NET 9 | Debug | 28 | 8 |
| .NET 9 | Release | 22 | 14 |
| .NET 10 | Debug | 28 | 8 |
| .NET 10 | Release | 22 | 14 |

Both `sync-control` and `concurrent-baseline` passed every GC combination in all
four runs. Managed-callback async scenarios continued to fail, including
`public-api`; individual passing cases varied between runs. A fresh concurrent
dump still shows an invalid method-table access in CoreCLR's GC mark queue.
Another fresh `public-api` dump faults in native future polling with a null
future address; the origin of that invalid value is not established.

On macOS Arm64, the component suite including all 36 stress cases passed
182/182 on .NET 9 Debug and .NET 10 Release at the default 20 iterations.
The Linux Arm64 .NET 9 Release component tests excluding this crash-isolated
matrix passed 146/146. Neither result establishes fiber/CLR compatibility:
Linux async managed-callback execution remains blocked by reproducible crashes.

For the separate native-runtime experiment using real OS threads rather than
alternate stacks, see [the thread-backed experiment](thread-backed/README.md).
Its custom library is opt-in and does not replace the packaged runtime.
On macOS that backend has a reproduced default Mach-port trap-handling failure.
`WASMTIME_FIBER_MACOS_MACH_PORTS=0` explicitly selects Unix signals for diagnostic
validation; it does not change production defaults or establish full compatibility.
