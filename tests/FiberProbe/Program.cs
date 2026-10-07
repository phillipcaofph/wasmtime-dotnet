using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Wasmtime;
using Wasmtime.Components;

namespace Wasmtime.FiberProbe;

internal static class Program
{
    private static int callbacks;
    private static int collections;
    private static int pendingPolls;
    private static int migrations;
    private static int nestedCalls;
    private static int recoveredTraps;
    private static int lifecycleCycles;
    private static int guestCollections;
    private static int stackOverflows;
    private static int causesTransferred;
    private static int storeGuardRejections;
    private static int ownerFuelOperations;
    private static object? benchmark;
    private static readonly ConcurrentDictionary<int, byte> callbackThreads = new();
    private static readonly ConcurrentDictionary<int, byte> pollingThreads = new();
    private static bool ThreadBackend =>
        Environment.GetEnvironmentVariable("WASMTIME_THREAD_FIBER_EXPERIMENT") == "1";
    private static bool ThreadAffine =>
        Environment.GetEnvironmentVariable("WASMTIME_FIBER_DIAGNOSTIC_AFFINITY") == "1";
    private static bool ForceGc =>
        Environment.GetEnvironmentVariable("WASMTIME_FIBER_DIAGNOSTIC_NO_FORCED_GC") != "1";

    private static async Task<int> Main(string[] args)
    {
        try
        {
            var scenario = args[0];
            var iterations = int.Parse(args[1]);
            if (NativeCallbackBridge.Enabled && ThreadBackend)
            {
                throw new InvalidOperationException("The callback bridge and thread backend are separate experiments.");
            }
            if (!NativeCallbackBridge.Enabled && scenario.StartsWith("bridge-") &&
                scenario is not ("bridge-values" or "bridge-bench"))
            {
                throw new InvalidOperationException($"{scenario} requires WASMTIME_CALLBACK_BRIDGE_EXPERIMENT=1.");
            }
            if (iterations < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(iterations));
            }
            var elapsed = Stopwatch.StartNew();
            if (ThreadBackend && (wasmtime_thread_fiber_started() != 0 || wasmtime_thread_fiber_live() != 0 ||
                wasmtime_thread_fiber_tls_suspensions() != 0))
            {
                throw new InvalidOperationException("Thread backend counters were not zero before execution.");
            }

            var config = new Config()
                .WithComponentModel(true)
                .WithComponentModelAsync(true)
                .WithFuelConsumption(true);
            bool? macosMachPorts = null;
            var machPorts = Environment.GetEnvironmentVariable("WASMTIME_FIBER_MACOS_MACH_PORTS");
            if (machPorts is not null)
            {
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX) || machPorts is not ("0" or "1"))
                {
                    throw new InvalidOperationException("WASMTIME_FIBER_MACOS_MACH_PORTS requires macOS and 0 or 1.");
                }
                macosMachPorts = machPorts == "1";
                config.WithMacosMachPorts(macosMachPorts.Value);
            }
            if (scenario == "guest-gc")
            {
                config.WithGc(true);
            }
            using var engine = new Engine(config);
            using var component = Component.FromTextFile(engine, Path.Combine(AppContext.BaseDirectory,
                scenario == "guest-gc" ? "fiber-gc-probe.wat" : "fiber-probe.wat"));

            if (scenario == "bridge-values")
            {
                await CheckBridgeValues(engine, iterations);
            }
            else if (scenario == "bridge-reentrant")
            {
                await CheckReentrant(engine, component, iterations);
            }
            else if (scenario == "bridge-exhaustion")
            {
                await CheckExhaustion(engine, component, iterations);
            }
            else if (scenario == "bridge-async")
            {
                await CheckAsyncImports(engine, component, iterations);
            }
            else if (scenario == "bridge-async-cancel")
            {
                await CheckAsyncCancellation(engine, iterations);
            }
            else if (scenario == "bridge-async-errors")
            {
                await CheckAsyncErrors(engine, component, iterations);
            }
            else if (scenario == "bridge-store-guard")
            {
                await CheckStoreGuard(engine, component, iterations);
            }
            else if (scenario == "bridge-bench")
            {
                benchmark = await RunBenchmark(engine, component, iterations);
            }
            else if (scenario == "bridge-registration")
            {
                using (var linker = CreateLinker(engine, false))
                using (var root = linker.Root())
                {
                    try
                    {
                        NativeCallbackBridge.DefineFunction(root, "observe", (_, _) => { });
                        throw new InvalidOperationException("Duplicate bridge registration was accepted.");
                    }
                    catch (WasmtimeException)
                    {
                        Interlocked.Increment(ref recoveredTraps);
                    }
                }
                await CheckPublicApi(engine, component, iterations, false);
            }
            else if (scenario == "stack-overflow")
            {
                await CheckStackOverflow(engine, iterations);
            }
            else if (scenario is "nested-sync" or "nested-async")
            {
                await CheckNested(engine, component, iterations, scenario == "nested-async");
            }
            else if (scenario == "lifecycle")
            {
                for (var i = 0; i < iterations; i++)
                {
                    await CheckCancellation(engine, component);
                    await CheckErrors(engine, component);
                    Interlocked.Increment(ref lifecycleCycles);
                    if (ThreadBackend && wasmtime_thread_fiber_live() != 0)
                    {
                        throw new InvalidOperationException("Execution threads survived a lifecycle cycle.");
                    }
                }
            }
            else if (scenario == "errors")
            {
                await CheckErrors(engine, component);
            }
            else if (scenario == "cancellation")
            {
                await CheckCancellation(engine, component);
            }
            else if (scenario is "public-api" or "startup")
            {
                await CheckPublicApi(engine, component, iterations, scenario == "startup");
            }
            else
            {
                var workers = scenario is "concurrent" or "concurrent-baseline" or "sync-control" or "guest-gc" or "shared-linker" ? 4 : 1;
                using var sharedLinker = scenario == "shared-linker" ? CreateLinker(engine, false) : null;
                using var barrier = new Barrier(workers);
                await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Task.Run(() =>
                {
                    using var store = new Store(engine);
                    store.Fuel = ulong.MaxValue;
                    using var ownedLinker = sharedLinker is null
                        ? CreateLinker(engine, false, scenario == "guest-gc" ? store : null)
                        : null;
                    var linker = sharedLinker ?? ownedLinker!;
                    var instance = linker.Instantiate(store, component);
                    if (scenario != "sync-control")
                    {
                        EnableYield(store);
                    }
                    using var first = new PollThread();
                    using var second = new PollThread();
                    for (var i = 0; i < iterations; i++)
                    {
                        if (scenario == "sync-control")
                        {
                            if (!barrier.SignalAndWait(TimeSpan.FromSeconds(30)))
                            {
                                throw new TimeoutException("Synchronous control workers did not rendezvous.");
                            }
                            if (instance.GetFunction("run")!.Call(ComponentValue.S32(100))!.AsS32() != 5050)
                            {
                                throw new InvalidOperationException("Synchronous control result was corrupted.");
                            }
                        }
                        else
                        {
                            RunYieldingCall(store, instance, scenario is not ("baseline" or "concurrent-baseline"),
                                first, second, barrier);
                        }
                    }
                })));
            }

            if (scenario is not ("errors" or "sync-control" or "stack-overflow" or "bridge-values" or
                "bridge-async-errors" or "bridge-bench") && pendingPolls == 0)
            {
                throw new InvalidOperationException("The probe never observed a pending native future.");
            }
            if (scenario is "callbacks" or "concurrent" &&
                (callbacks == 0 || (ForceGc && collections == 0) || (!ThreadAffine && migrations == 0)))
            {
                throw new InvalidOperationException("Callback/GC/thread-migration coverage was not exercised.");
            }
            var nativeThreadsStarted = ThreadBackend ? wasmtime_thread_fiber_started() : 0;
            var nativeThreadsLive = ThreadBackend ? wasmtime_thread_fiber_live() : 0;
            var nativeTlsSuspensions = ThreadBackend ? wasmtime_thread_fiber_tls_suspensions() : 0;
            NativeCallbackBridge.Finish();
            ulong Bridge(NativeCallbackBridge.BridgeCounter counter) =>
                NativeCallbackBridge.Enabled ? NativeCallbackBridge.Counter(counter) : 0;
            var bridgeRequests = Bridge(NativeCallbackBridge.BridgeCounter.CompletedRequests);
            if (NativeCallbackBridge.Enabled &&
                (bridgeRequests < (ulong)callbacks || callbackThreads.Keys.Any(pollingThreads.ContainsKey)))
            {
                throw new InvalidOperationException("Callback bridge coverage or dispatcher isolation failed.");
            }
            if (NativeCallbackBridge.Enabled && NativeCallbackBridge.PendingCauses != 0)
            {
                throw new InvalidOperationException(
                    $"{NativeCallbackBridge.PendingCauses} host failure(s) were never reported to a caller.");
            }
            if (ThreadBackend && scenario is not ("errors" or "sync-control" or "stack-overflow") &&
                nativeTlsSuspensions == 0)
            {
                throw new InvalidOperationException("No parked worker activation list was captured.");
            }
            if (ThreadBackend && ((scenario == "sync-control" ? nativeThreadsStarted != 0 : nativeThreadsStarted == 0) ||
                nativeThreadsLive != 0 ||
                callbackThreads.Keys.Any(pollingThreads.ContainsKey)))
            {
                throw new InvalidOperationException(
                    $"Thread backend coverage/lifetime failure: started={nativeThreadsStarted}, live={nativeThreadsLive}.");
            }

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                scenario, iterations, callbacks, collections, pendingPolls, migrations,
                framework = RuntimeInformation.FrameworkDescription,
                os = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                processorCount = Environment.ProcessorCount,
                wasmtimeAssembly = typeof(Engine).Assembly.GetName().Version?.ToString(),
                elapsedMilliseconds = elapsed.ElapsedMilliseconds,
                serverGc = GCSettings.IsServerGC,
                backgroundGc = Environment.GetEnvironmentVariable("DOTNET_gcConcurrent"),
                gcStress = Environment.GetEnvironmentVariable("DOTNET_GCStress"),
                threadAffine = ThreadAffine,
                forcedGc = ForceGc,
                threadBackend = ThreadBackend,
                callbackBridge = NativeCallbackBridge.Enabled,
                bridgeRequests,
                bridgesLive = Bridge(NativeCallbackBridge.BridgeCounter.LiveRegistrations),
                bridgeLiveAsync = Bridge(NativeCallbackBridge.BridgeCounter.LiveAsync),
                bridgeThreadsStarted = Bridge(NativeCallbackBridge.BridgeCounter.ThreadsStarted),
                bridgePeakWorkers = Bridge(NativeCallbackBridge.BridgeCounter.PeakBusyWorkers),
                bridgeExhaustions = Bridge(NativeCallbackBridge.BridgeCounter.Exhaustions),
                bridgeOwnerOperations = Bridge(NativeCallbackBridge.BridgeCounter.OwnerOperations),
                bridgePeakAsync = Bridge(NativeCallbackBridge.BridgeCounter.PeakLiveAsync),
                bridgeAsyncCancelled = Bridge(NativeCallbackBridge.BridgeCounter.AsyncCancelled),
                bridgeStagedDeleted = Bridge(NativeCallbackBridge.BridgeCounter.StagedDeleted),
                bridgeLateFailures = Bridge(NativeCallbackBridge.BridgeCounter.LateFailures),
                bridgePoisonedResults = Bridge(NativeCallbackBridge.BridgeCounter.PoisonedResults),
                bridgePendingCauses = NativeCallbackBridge.Enabled ? NativeCallbackBridge.PendingCauses : 0,
                storeGuardRejections,
                ownerFuelOperations,
                benchmark,
                causesTransferred,
                macosMachPorts,
                nativeThreadsStarted,
                nativeThreadsLive,
                nativeTlsSuspensions,
                callbackThreadCount = callbackThreads.Count,
                pollingThreadCount = pollingThreads.Count,
                nestedCalls,
                recoveredTraps,
                lifecycleCycles,
                guestCollections,
                stackOverflows,
                status = "passed"
            }));
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            NativeCallbackBridge.Finish();
        }
    }

    private static ComponentLinker CreateLinker(Engine engine, bool throws, Store? collectGuest = null)
    {
        var linker = new ComponentLinker(engine);
        using var root = linker.Root();
        NativeCallbackBridge.DefineFunction(root, "observe", (arguments, results) =>
        {
            if (throws)
            {
                throw new InvalidOperationException("fiber-probe host failure");
            }

            CheckManagedCallback();
            if (collectGuest is not null)
            {
                NativeCallbackBridge.CollectGuest(collectGuest);
                Interlocked.Increment(ref guestCollections);
            }
            results[0] = ComponentValue.S32(arguments[0].AsS32());
        });
        return linker;
    }

    private static void CheckManagedCallback()
    {
        Interlocked.Increment(ref callbacks);
        if (ThreadBackend || NativeCallbackBridge.Enabled)
        {
            callbackThreads.TryAdd(Environment.CurrentManagedThreadId, 0);
        }
        CheckRoots();
    }

    private static async Task CheckBridgeValues(Engine engine, int iterations)
    {
        using var component = Component.FromTextFile(
            engine, Path.Combine(AppContext.BaseDirectory, "bridge-values.wat"));
        using var store = new Store(engine);
        store.Fuel = ulong.MaxValue;
        using var linker = new ComponentLinker(engine);
        using (var root = linker.Root())
        {
            foreach (var name in new[] { "text", "bytes" })
            {
                NativeCallbackBridge.DefineFunction(root, name, (arguments, results) =>
                {
                    CheckManagedCallback();
                    results[0] = arguments[0];
                });
            }
        }
        var instance = linker.Instantiate(store, component);
        var text = instance.GetFunction("text")!;
        var bytes = instance.GetFunction("bytes")!;
        for (var i = 0; i < iterations; i++)
        {
            var expected = i % 2 == 0 ? "" : "bridge \u03bb \ud83d\ude80\0" + new string('x', 4096);
            if ((await text.CallAsync(ComponentValue.String(expected)))!.AsString() != expected)
            {
                throw new InvalidOperationException("Bridged string was corrupted.");
            }
            var values = Enumerable.Range(0, i % 2 == 0 ? 0 : 256)
                .Select(value => ComponentValue.U8((byte)value)).ToArray();
            var echoed = (await bytes.CallAsync(ComponentValue.List(values)))!.AsList();
            if (echoed.Count != values.Length ||
                echoed.Where((value, index) => value.AsU8() != values[index].AsU8()).Any())
            {
                throw new InvalidOperationException("Bridged list was corrupted.");
            }
        }
    }

    private const string RecursiveComponent = """
        (component
          (core module $m
            (func $recurse (export "recurse")
              call $recurse
              nop))
          (core instance $i (instantiate $m))
          (func (export "recurse") (canon lift (core func $i "recurse"))))
        """;

    private static async Task CheckStackOverflow(Engine engine, int iterations)
    {
        using var recursive = Component.FromText(engine, RecursiveComponent);
        using var linker = new ComponentLinker(engine);
        for (var i = 0; i < iterations; i++)
        {
            using var store = new Store(engine);
            store.Fuel = ulong.MaxValue;
            var function = linker.Instantiate(store, recursive).GetFunction("recurse")!;
            try
            {
                await function.CallAsync();
                throw new InvalidOperationException("Recursive guest did not trap on stack exhaustion.");
            }
            catch (WasmtimeException error) when (error.Message.Contains("call stack exhausted"))
            {
                Interlocked.Increment(ref stackOverflows);
            }
        }
    }

    private static async Task CheckNested(Engine engine, Component component, int iterations, bool asyncInner)
    {
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            using var innerStore = new Store(engine);
            innerStore.Fuel = ulong.MaxValue;
            using var innerLinker = CreateLinker(engine, false);
            var inner = innerLinker.Instantiate(innerStore, component);
            if (asyncInner)
            {
                EnableYield(innerStore);
            }
            var innerRun = inner.GetFunction("run")!;
            var innerSpin = inner.GetFunction("spin")!;
            using var outerStore = new Store(engine);
            outerStore.Fuel = ulong.MaxValue;
            using var outerLinker = new ComponentLinker(engine);
            using (var root = outerLinker.Root())
            {
                NativeCallbackBridge.DefineFunction(root, "observe", (arguments, results) =>
                {
                    var sentinel = new Sentinel();
                    CheckManagedCallback();
                    var value = asyncInner
                        ? innerRun.CallAsync(ComponentValue.S32(3)).GetAwaiter().GetResult()
                        : innerRun.Call(ComponentValue.S32(3));
                    if (value!.AsS32() != 6)
                    {
                        throw new InvalidOperationException("Nested call result was corrupted.");
                    }
                    // Component traps can poison Store entry state; use a disposable trap Store.
                    using var trapStore = new Store(engine);
                    trapStore.Fuel = ulong.MaxValue;
                    var trapInstance = innerLinker.Instantiate(trapStore, component);
                    if (asyncInner)
                    {
                        EnableYield(trapStore);
                    }
                    var innerTrap = trapInstance.GetFunction("boom")!;
                    try
                    {
                        if (asyncInner)
                        {
                            innerTrap.CallAsync().GetAwaiter().GetResult();
                        }
                        else
                        {
                            innerTrap.Call();
                        }
                        throw new InvalidOperationException("Nested guest trap was not surfaced.");
                    }
                    catch (WasmtimeException error) when (error.Message.Contains("unreachable"))
                    {
                        Interlocked.Increment(ref recoveredTraps);
                    }
                    var recovered = asyncInner
                        ? innerSpin.CallAsync(ComponentValue.S32(100)).GetAwaiter().GetResult()
                        : innerSpin.Call(ComponentValue.S32(100));
                    if (recovered!.AsS32() != 42)
                    {
                        throw new InvalidOperationException("Nested Store failed after a trap.");
                    }
                    sentinel.Verify();
                    GC.KeepAlive(sentinel);
                    Interlocked.Increment(ref nestedCalls);
                    results[0] = ComponentValue.S32(arguments[0].AsS32());
                });
            }
            var outer = outerLinker.Instantiate(outerStore, component).GetFunction("run")!;
            EnableYield(outerStore);
            for (var i = 0; i < iterations; i++)
            {
                var call = outer.CallAsync(ComponentValue.S32(100));
                if (call.IsCompleted)
                {
                    await call;
                    throw new InvalidOperationException("Nested outer call never suspended.");
                }
                Interlocked.Increment(ref pendingPolls);
                if ((await call)!.AsS32() != 5050)
                {
                    throw new InvalidOperationException("Nested outer result was corrupted.");
                }
            }
        })));
    }

    private static void ExpectCause<T>(Store store, WasmtimeException error, string message)
        where T : Exception
    {
        if (!NativeCallbackBridge.Enabled)
        {
            return;
        }
        // The bridge records the original exception by Store context; the library attaches it.
        var cause = error.InnerException;
        if (cause is not T || !cause.Message.Contains(message))
        {
            throw new InvalidOperationException($"Callback exception was not transferred: {cause}", error);
        }
        Interlocked.Increment(ref causesTransferred);
    }

    private static async Task CheckReentrant(Engine engine, Component component, int iterations)
    {
        using var recursive = Component.FromText(engine, RecursiveComponent);
        using var linker = new ComponentLinker(engine);
        ComponentFunction? outerRun = null;
        var depth = 0;
        using (var root = linker.Root())
        {
            // A single registration re-entered through other Stores needs more than one worker.
            NativeCallbackBridge.DefineFunction(root, "observe", (arguments, results) =>
            {
                CheckManagedCallback();
                if (arguments[0].AsS32() == 3)
                {
                    var level = Interlocked.Increment(ref depth);
                    try
                    {
                        if (level == 1)
                        {
                            try
                            {
                                outerRun!.Call(ComponentValue.S32(1));
                                throw new InvalidOperationException("Same-Store re-entry was permitted.");
                            }
                            catch (InvalidOperationException error) when (error.Message.Contains("already in progress") ||
                                error.Message.Contains("isolated host callback"))
                            {
                                Interlocked.Increment(ref recoveredTraps);
                            }
                            using var deep = new Store(engine);
                            deep.Fuel = ulong.MaxValue;
                            try
                            {
                                linker.Instantiate(deep, recursive).GetFunction("recurse")!.Call();
                                throw new InvalidOperationException("Worker-thread recursion did not trap.");
                            }
                            catch (WasmtimeException error) when (error.Message.Contains("call stack exhausted"))
                            {
                                Interlocked.Increment(ref stackOverflows);
                            }
                        }
                        if (level < 4)
                        {
                            using var nestedStore = new Store(engine);
                            nestedStore.Fuel = ulong.MaxValue;
                            var nested = linker.Instantiate(nestedStore, component).GetFunction("run")!;
                            if (nested.Call(ComponentValue.S32(3))!.AsS32() != 6)
                            {
                                throw new InvalidOperationException("Re-entrant result was corrupted.");
                            }
                            Interlocked.Increment(ref nestedCalls);
                        }
                    }
                    finally
                    {
                        Interlocked.Decrement(ref depth);
                    }
                }
                results[0] = arguments[0];
            });
        }
        for (var i = 0; i < iterations; i++)
        {
            using var store = new Store(engine);
            store.Fuel = ulong.MaxValue;
            outerRun = linker.Instantiate(store, component).GetFunction("run")!;
            EnableYield(store);
            var call = outerRun.CallAsync(ComponentValue.S32(10));
            if (!call.IsCompleted)
            {
                Interlocked.Increment(ref pendingPolls);
            }
            if ((await call)!.AsS32() != 55)
            {
                throw new InvalidOperationException("Re-entrant outer result was corrupted.");
            }
        }
    }

    private static async Task CheckExhaustion(Engine engine, Component component, int iterations)
    {
        if (NativeCallbackBridge.MaxWorkers != 1)
        {
            throw new InvalidOperationException("bridge-exhaustion requires WASMTIME_BRIDGE_MAX_WORKERS=1.");
        }
        using var linker = new ComponentLinker(engine);
        using (var root = linker.Root())
        {
            NativeCallbackBridge.DefineFunction(root, "observe", (arguments, results) =>
            {
                CheckManagedCallback();
                if (arguments[0].AsS32() == 3)
                {
                    using var nestedStore = new Store(engine);
                    nestedStore.Fuel = ulong.MaxValue;
                    var nested = linker.Instantiate(nestedStore, component).GetFunction("run")!;
                    try
                    {
                        nested.Call(ComponentValue.S32(3));
                        throw new InvalidOperationException("A nested callback ran without a worker.");
                    }
                    catch (WasmtimeException error) when (error.Message.Contains("worker pool exhausted"))
                    {
                        // The only worker is busy here; waiting would deadlock, so the stub fails fast.
                        Interlocked.Increment(ref recoveredTraps);
                    }
                }
                results[0] = arguments[0];
            });
        }
        for (var i = 0; i < iterations; i++)
        {
            using var store = new Store(engine);
            store.Fuel = ulong.MaxValue;
            var run = linker.Instantiate(store, component).GetFunction("run")!;
            EnableYield(store);
            var call = run.CallAsync(ComponentValue.S32(10));
            if (!call.IsCompleted)
            {
                Interlocked.Increment(ref pendingPolls);
            }
            if ((await call)!.AsS32() != 55)
            {
                throw new InvalidOperationException("Outer call failed after pool exhaustion.");
            }
        }
    }

    private static void CountAsyncCallback()
    {
        if (Interlocked.Increment(ref callbacks) % 16 == 0 && ForceGc)
        {
            CheckRoots();
        }
        callbackThreads.TryAdd(Environment.CurrentManagedThreadId, 0);
    }

    private static async Task CheckAsyncImports(Engine engine, Component component, int iterations)
    {
        using var values = Component.FromTextFile(
            engine, Path.Combine(AppContext.BaseDirectory, "bridge-values.wat"));
        using var linker = new ComponentLinker(engine);
        using (var root = linker.Root())
        {
            NativeCallbackBridge.DefineAsyncFunction(root, "observe", async (arguments, cancellation) =>
            {
                CountAsyncCallback();
                var value = arguments[0].AsS32();
                await Task.Delay(1 + value % 3, cancellation);
                return new[] { ComponentValue.S32(value) };
            });
            foreach (var name in new[] { "text", "bytes" })
            {
                NativeCallbackBridge.DefineAsyncFunction(root, name, async (arguments, cancellation) =>
                {
                    CountAsyncCallback();
                    await Task.Yield();
                    return new[] { arguments[0] };
                });
            }
        }
        const int stores = 32;
        for (var i = 0; i < iterations; i++)
        {
            await Task.WhenAll(Enumerable.Range(0, stores).Select(index => Task.Run(async () =>
            {
                using var store = new Store(engine);
                store.Fuel = ulong.MaxValue;
                if (index % 2 == 0)
                {
                    var call = (await linker.InstantiateAsync(store, component)).GetFunction("run")!
                        .CallAsync(ComponentValue.S32(20));
                    if (!call.IsCompleted)
                    {
                        Interlocked.Increment(ref pendingPolls);
                    }
                    if ((await call)!.AsS32() != 210)
                    {
                        throw new InvalidOperationException("Async import result was corrupted.");
                    }
                    return;
                }
                var instance = (await linker.InstantiateAsync(store, values));
                var expected = "async \u03bb \ud83d\ude80\0" + new string((char)('a' + index % 26), 1024 + index);
                if ((await instance.GetFunction("text")!.CallAsync(ComponentValue.String(expected)))!
                    .AsString() != expected)
                {
                    throw new InvalidOperationException("Async staged string was corrupted.");
                }
                var bytes = Enumerable.Range(0, 300).Select(value => ComponentValue.U8((byte)(value + index))).ToArray();
                var echoed = (await instance.GetFunction("bytes")!.CallAsync(ComponentValue.List(bytes)))!.AsList();
                if (echoed.Count != bytes.Length || echoed.Where((value, at) => value.AsU8() != bytes[at].AsU8()).Any())
                {
                    throw new InvalidOperationException("Async staged list was corrupted.");
                }
            })));
        }
    }

    private static async Task CheckAsyncCancellation(Engine engine, int iterations)
    {
        using var component = Component.FromTextFile(
            engine, Path.Combine(AppContext.BaseDirectory, "bridge-values.wat"));
        using var linker = new ComponentLinker(engine);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = 0;
        using (var root = linker.Root())
        {
            NativeCallbackBridge.DefineAsyncFunction(root, "text", async (arguments, cancellation) =>
            {
                CountAsyncCallback();
                Volatile.Read(ref started).TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellation);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref observed);
                }
                // Returning owned values after Wasmtime dropped the future must not leak or write freed memory.
                return new[] { ComponentValue.String(new string('z', 8192)) };
            });
            NativeCallbackBridge.DefineAsyncFunction(root, "bytes", (arguments, _) =>
                Task.FromResult(new[] { arguments[0] }));
        }
        for (var i = 0; i < iterations; i++)
        {
            Volatile.Write(ref started, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            var before = Volatile.Read(ref observed);
            using (var store = new Store(engine))
            {
                store.Fuel = ulong.MaxValue;
                var text = (await linker.InstantiateAsync(store, component)).GetFunction("text")!;
                using var cancellation = new CancellationTokenSource();
                var call = text.CallAsync(new[] { ComponentValue.String("cancel me") }, cancellation.Token);
                await Volatile.Read(ref started).Task.WaitAsync(TimeSpan.FromSeconds(30));
                Interlocked.Increment(ref pendingPolls);
                cancellation.Cancel();
                try
                {
                    await call;
                    throw new InvalidOperationException("Cancelled async import completed.");
                }
                catch (OperationCanceledException)
                {
                }
            }
            // The future (or at the latest its Store) is gone; the callback must have been told.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (Volatile.Read(ref observed) == before ||
                NativeCallbackBridge.Counter(NativeCallbackBridge.BridgeCounter.LiveAsync) != 0)
            {
                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("Async cancellation was not propagated or released.");
                }
                await Task.Delay(5);
            }
            Interlocked.Increment(ref lifecycleCycles);
        }
    }

    private const string LateComponent = """
        (component
          (import "notify" (func $notify (param "x" s32)))
          (import "check" (func $check (param "x" s32) (result bool)))
          (core func $lower-notify (canon lower (func $notify)))
          (core func $lower-check (canon lower (func $check)))
          (core module $m
            (import "" "notify" (func $notify (param i32)))
            (import "" "check" (func $check (param i32) (result i32)))
            (func (export "notify") (param $x i32) (result i32)
              local.get $x
              call $notify
              local.get $x
              i32.const 1
              i32.add)
            (func (export "check") (param $x i32) (result i32)
              local.get $x
              call $check))
          (core instance $i (instantiate $m
            (with "" (instance
              (export "notify" (func $lower-notify))
              (export "check" (func $lower-check))))))
          (func (export "notify") (param "x" s32) (result s32)
            (canon lift (core func $i "notify")))
          (func (export "check") (param "x" s32) (result bool)
            (canon lift (core func $i "check"))))
        """;

    private enum FailureMode { None, Early, Late }

    private static async Task CheckAsyncErrors(Engine engine, Component component, int iterations)
    {
        using var shapes = Component.FromText(engine, LateComponent);
        using var linker = new ComponentLinker(engine);
        var mode = (int)FailureMode.None;
        // Released once CallAsync has returned, which happens only after the guest suspended in the
        // import, so a late failure can never be observed synchronously however slow the worker is.
        var lateGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Fail(CancellationToken cancellation)
        {
            CountAsyncCallback();
            switch ((FailureMode)Volatile.Read(ref mode))
            {
                case FailureMode.Early:
                    throw new FormatException("early async failure");
                case FailureMode.Late:
                    await Volatile.Read(ref lateGate).Task;
                    throw new TimeoutException("late async failure");
                default:
                    await Task.Delay(1, cancellation);
                    break;
            }
        }
        using (var root = linker.Root())
        {
            NativeCallbackBridge.DefineAsyncFunction(root, "observe", async (arguments, cancellation) =>
            {
                await Fail(cancellation);
                return new[] { arguments[0] };
            });
            NativeCallbackBridge.DefineAsyncFunction(root, "notify", async (_, cancellation) =>
            {
                await Fail(cancellation);
                return Array.Empty<ComponentValue>();
            });
            NativeCallbackBridge.DefineAsyncFunction(root, "check", async (arguments, cancellation) =>
            {
                await Fail(cancellation);
                return new[] { ComponentValue.Bool(arguments[0].AsS32() % 2 == 0) };
            });
        }
        var cases = new (string Export, bool Shapes, ComponentValue Argument, Func<ComponentValue?, bool> Expected)[]
        {
            ("run", false, ComponentValue.S32(1), value => value!.AsS32() == 1),
            ("notify", true, ComponentValue.S32(4), value => value!.AsS32() == 5),
            ("check", true, ComponentValue.S32(4), value => value!.AsBool()),
        };
        for (var i = 0; i < iterations; i++)
        {
            foreach (var (export, usesShapes, argument, expected) in cases)
            {
                foreach (var failure in new[] { FailureMode.None, FailureMode.Early, FailureMode.Late })
                {
                    Volatile.Write(ref mode, (int)failure);
                    var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    Volatile.Write(ref lateGate, gate);
                    using var store = new Store(engine);
                    store.Fuel = ulong.MaxValue;
                    var function = (await linker.InstantiateAsync(store, usesShapes ? shapes : component))
                        .GetFunction(export)!;
                    try
                    {
                        var pending = function.CallAsync(argument);
                        gate.TrySetResult(true);
                        var result = await pending;
                        if (failure != FailureMode.None)
                        {
                            throw new InvalidOperationException($"{export}: async import failure was swallowed.");
                        }
                        if (!expected(result))
                        {
                            throw new InvalidOperationException($"{export}: async import result was corrupted.");
                        }
                        continue;
                    }
                    catch (WasmtimeException error) when (failure != FailureMode.None)
                    {
                        if (Environment.GetEnvironmentVariable("WASMTIME_BRIDGE_TRACE") == "1")
                        {
                            Console.Error.WriteLine($"{export}/{failure} surfaced as: {error.Message}");
                        }
                        if (failure == FailureMode.Early)
                        {
                            if (!error.Message.Contains("early async failure"))
                            {
                                throw;
                            }
                            ExpectCause<FormatException>(store, error, "early async failure");
                        }
                        else
                        {
                            // A zero-result import cannot trap the guest, so the caller rethrows after it returns.
                            if (export == "notify" && !error.Message.Contains("failed after the call suspended"))
                            {
                                throw;
                            }
                            ExpectCause<TimeoutException>(store, error, "late async failure");
                        }
                        Interlocked.Increment(ref recoveredTraps);
                    }
                    // The failed Store must still be usable for independent calls when it did not trap.
                    if (export == "notify" && failure == FailureMode.Late)
                    {
                        Volatile.Write(ref mode, (int)FailureMode.None);
                        if ((await function.CallAsync(ComponentValue.S32(9)))!.AsS32() != 10)
                        {
                            throw new InvalidOperationException("Store failed after a reported late failure.");
                        }
                    }
                    using var fresh = new Store(engine);
                    fresh.Fuel = ulong.MaxValue;
                    if ((await (await linker.InstantiateAsync(fresh, component)).GetFunction("spin")!
                        .CallAsync(ComponentValue.S32(10)))!.AsS32() != 42)
                    {
                        throw new InvalidOperationException("Fresh Store failed after an async import error.");
                    }
                }
            }
        }
    }

    private static async Task CheckStoreGuard(Engine engine, Component component, int iterations)
    {
        using var values = Component.FromTextFile(
            engine, Path.Combine(AppContext.BaseDirectory, "bridge-values.wat"));
        Store? active = null;
        void ExpectRejected(Action access)
        {
            try
            {
                access();
                throw new InvalidOperationException("Direct Store access from a bridged callback was permitted.");
            }
            catch (InvalidOperationException error) when (error.Message.Contains("isolated host callback"))
            {
                Interlocked.Increment(ref storeGuardRejections);
            }
        }
        using var linker = new ComponentLinker(engine);
        using (var root = linker.Root())
        {
            NativeCallbackBridge.DefineFunction(root, "observe", (arguments, results) =>
            {
                CheckManagedCallback();
                var store = Volatile.Read(ref active)!;
                ExpectRejected(() => _ = store.Context);
                ExpectRejected(() => store.SetWasiConfiguration(new WasiConfiguration()));
                // Fuel, GC and epoch deadlines transparently run on the owning Wasmtime activation.
                var fuel = store.Fuel;
                if (fuel == 0)
                {
                    throw new InvalidOperationException("Proxied fuel read returned zero.");
                }
                store.Fuel = ulong.MaxValue;
                if (store.Fuel < fuel)
                {
                    throw new InvalidOperationException("Proxied fuel write was lost.");
                }
                Interlocked.Add(ref ownerFuelOperations, 3);
                store.GC();
                store.SetEpochDeadline(1_000_000);
                // Stores the callback is not serving remain directly usable.
                using var other = new Store(engine);
                other.Fuel = 7;
                if (other.Fuel != 7)
                {
                    throw new InvalidOperationException("Unrelated Store access was blocked.");
                }
                results[0] = arguments[0];
            });
        }
        using var asyncLinker = new ComponentLinker(engine);
        using (var root = asyncLinker.Root())
        {
            foreach (var name in new[] { "text", "bytes" })
            {
                NativeCallbackBridge.DefineAsyncFunction(root, name, async (arguments, cancellation) =>
                {
                    CountAsyncCallback();
                    var store = Volatile.Read(ref active)!;
                    ExpectRejected(() => _ = store.Context);
                    // Before the first yield the owning activation still waits, so owner operations work.
                    if (store.Fuel == 0)
                    {
                        throw new InvalidOperationException("Proxied async fuel read returned zero.");
                    }
                    store.GC();
                    Interlocked.Add(ref ownerFuelOperations, 2);
                    // A short delay can finish before it is awaited (for example under GCStress), so
                    // force the yield the expected counts depend on.
                    await Task.Yield();
                    await Task.Delay(1, cancellation);
                    // After yielding, the poll loop driving the call serves owner operations between polls.
                    ExpectRejected(() => _ = store.Context);
                    if (store.Fuel == 0)
                    {
                        throw new InvalidOperationException("Deferred async fuel read returned zero.");
                    }
                    store.GC();
                    Interlocked.Add(ref ownerFuelOperations, 2);
                    return new[] { arguments[0] };
                });
            }
        }
        for (var i = 0; i < iterations; i++)
        {
            using (var store = new Store(engine))
            {
                store.Fuel = ulong.MaxValue;
                Volatile.Write(ref active, store);
                var run = linker.Instantiate(store, component).GetFunction("run")!;
                EnableYield(store);
                var call = run.CallAsync(ComponentValue.S32(3));
                if (!call.IsCompleted)
                {
                    Interlocked.Increment(ref pendingPolls);
                }
                if ((await call)!.AsS32() != 6)
                {
                    throw new InvalidOperationException("Guarded call result was corrupted.");
                }
                // The guard is scoped to the callback, not left on the calling context.
                _ = store.Fuel;
            }
            using (var store = new Store(engine))
            {
                store.Fuel = ulong.MaxValue;
                Volatile.Write(ref active, store);
                var text = (await asyncLinker.InstantiateAsync(store, values)).GetFunction("text")!;
                var call = text.CallAsync(ComponentValue.String("guarded"));
                if (!call.IsCompleted)
                {
                    Interlocked.Increment(ref pendingPolls);
                }
                if ((await call)!.AsString() != "guarded")
                {
                    throw new InvalidOperationException("Guarded async result was corrupted.");
                }
                _ = store.Fuel;
            }
        }
    }

    private static async Task<object> RunBenchmark(Engine engine, Component component, int iterations)
    {
        const int importsPerCall = 1000;
        async Task<double> Measure(ComponentLinker linker, bool instantiateAsync)
        {
            using var store = new Store(engine);
            store.Fuel = ulong.MaxValue;
            var run = (instantiateAsync
                ? await linker.InstantiateAsync(store, component)
                : linker.Instantiate(store, component)).GetFunction("run")!;
            // Warm up the worker pool, JIT and native stubs.
            await run.CallAsync(ComponentValue.S32(importsPerCall));
            var timer = Stopwatch.StartNew();
            for (var i = 0; i < iterations; i++)
            {
                if ((await run.CallAsync(ComponentValue.S32(importsPerCall)))!.AsS32() !=
                    importsPerCall * (importsPerCall + 1) / 2)
                {
                    throw new InvalidOperationException("Benchmark result was corrupted.");
                }
            }
            return timer.Elapsed.TotalMilliseconds * 1_000_000 / ((double)iterations * importsPerCall);
        }
        using var sync = new ComponentLinker(engine);
        using (var root = sync.Root())
        {
            NativeCallbackBridge.DefineFunction(root, "observe", (arguments, results) => results[0] = arguments[0]);
        }
        var syncNs = await Measure(sync, false);
        double? readyNs = null, yieldNs = null;
        if (NativeCallbackBridge.Enabled)
        {
            using var ready = new ComponentLinker(engine);
            using (var root = ready.Root())
            {
                NativeCallbackBridge.DefineAsyncFunction(root, "observe", (arguments, _) =>
                    Task.FromResult(new[] { arguments[0] }));
            }
            readyNs = await Measure(ready, true);
            using var yielding = new ComponentLinker(engine);
            using (var root = yielding.Root())
            {
                NativeCallbackBridge.DefineAsyncFunction(root, "observe", async (arguments, _) =>
                {
                    await Task.Yield();
                    return new[] { arguments[0] };
                });
            }
            yieldNs = await Measure(yielding, true);
        }
        return new
        {
            importsPerCall,
            calls = iterations,
            waker = Environment.GetEnvironmentVariable("WASMTIME_BRIDGE_WAKER") != "0",
            syncImportNs = Math.Round(syncNs),
            asyncReadyImportNs = readyNs is { } r ? Math.Round(r) : (double?)null,
            asyncYieldImportNs = yieldNs is { } y ? Math.Round(y) : (double?)null,
        };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckRoots()
    {
        var sentinel = new Sentinel();
        var weak = new WeakReference<Sentinel>(sentinel);
        for (var i = 0; i < 8; i++)
        {
            GC.KeepAlive(new byte[32 * 1024]);
        }
        if (ForceGc)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            Interlocked.Increment(ref collections);
        }
        if (!weak.TryGetTarget(out var observed) || Volatile.Read(ref observed.Finalized) != 0)
        {
            throw new InvalidOperationException("A live callback-local object was collected/finalized.");
        }
        sentinel.Verify();
        GC.KeepAlive(sentinel);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunYieldingCall(Store store, ComponentInstance instance, bool managedImports,
        PollThread first, PollThread second, Barrier barrier)
    {
        var sentinel = new Sentinel();
        using var call = new NativeCall(store, instance, managedImports ? "run" : "spin", 100);
        if (!barrier.SignalAndWait(TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException("Concurrent probe workers did not rendezvous with live futures.");
        }
        var pending = 0;
        var threadIds = new HashSet<int>();
        for (var poll = 0; ; poll++)
        {
            if (poll == 10000)
            {
                throw new TimeoutException("Native future exceeded the poll budget.");
            }
            var worker = ThreadAffine || poll % 2 == 0 ? first : second;
            var ready = worker.Poll(call.Future);
            threadIds.Add(worker.ThreadId);
            if (ready)
            {
                break;
            }
            pending++;
            if (ForceGc)
            {
                GC.Collect();
            }
            sentinel.Verify();
        }

        var result = call.Complete();
        if (result != (managedImports ? 5050 : 42) || pending == 0 ||
            threadIds.Count != (ThreadAffine ? 1 : 2))
        {
            throw new InvalidOperationException(
                $"Unexpected result/coverage: result={result}, pending={pending}, threads={threadIds.Count}.");
        }
        Interlocked.Add(ref pendingPolls, pending);
        if (!ThreadAffine)
        {
            Interlocked.Increment(ref migrations);
        }
        sentinel.Verify();
        GC.KeepAlive(sentinel);
    }

    private static async Task CheckCancellation(Engine engine, Component component)
    {
        using var store = new Store(engine);
        using var linker = CreateLinker(engine, false);
        store.Fuel = ulong.MaxValue;
        var instance = linker.Instantiate(store, component);
        EnableYield(store);
        using var cancellation = new CancellationTokenSource();
        var run = instance.GetFunction("run")!;
        var task = run.CallAsync(
            new[] { ComponentValue.S32(int.MaxValue) }, cancellation.Token);
        if (task.IsCompleted)
        {
            throw new InvalidOperationException("Cancellation probe did not suspend.");
        }
        Interlocked.Increment(ref pendingPolls);
        try
        {
            store.Dispose();
            throw new InvalidOperationException("Disposal was permitted while a call was suspended.");
        }
        catch (InvalidOperationException error) when (error.Message.Contains("cannot be disposed"))
        {
        }
        try
        {
            run.Call(ComponentValue.S32(10));
            throw new InvalidOperationException("Overlapping component execution was permitted.");
        }
        catch (InvalidOperationException error) when (error.Message.Contains("already in progress"))
        {
        }
        cancellation.Cancel();
        try
        {
            await task;
            throw new InvalidOperationException("Cancellation was ignored.");
        }
        catch (OperationCanceledException)
        {
        }
        // A canceled guest may not be reusable. A fresh store must still work.
        using var fresh = new Store(engine);
        fresh.Fuel = ulong.MaxValue;
        var add = linker.Instantiate(fresh, component).GetFunction("spin")!;
        if ((await add.CallAsync(ComponentValue.S32(10)))!.AsS32() != 42)
        {
            throw new InvalidOperationException("Fresh store failed after cancellation.");
        }
    }

    private static async Task CheckPublicApi(Engine engine, Component component, int iterations, bool startup)
    {
        await Task.Yield();
        var sentinel = new Sentinel();
        using var linker = CreateLinker(engine, false);
        using var withStart = startup
            ? Component.FromText(engine, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fiber-probe.wat"))
                .Replace("(func $boom", """
                    (func $start
                      i32.const 100
                      call $run
                      drop)
                    (start $start)
                    (func $boom
                    """))
            : null;
        for (var i = 0; i < iterations; i++)
        {
            using var store = new Store(engine);
            store.Fuel = ulong.MaxValue;
            if (startup)
            {
                EnableYield(store);
                var instantiate = linker.InstantiateAsync(store, withStart!);
                if (instantiate.IsCompleted)
                {
                    throw new InvalidOperationException("Async startup did not suspend.");
                }
                Interlocked.Increment(ref pendingPolls);
                GC.Collect();
                sentinel.Verify();
                await instantiate;
            }
            else
            {
                var instance = linker.Instantiate(store, component);
                EnableYield(store);
                var task = instance.GetFunction("run")!.CallAsync(ComponentValue.S32(100));
                if (task.IsCompleted)
                {
                    throw new InvalidOperationException("Public async call did not suspend.");
                }
                Interlocked.Increment(ref pendingPolls);
                GC.Collect();
                sentinel.Verify();
                if ((await task)!.AsS32() != 5050)
                {
                    throw new InvalidOperationException("Public async call returned a corrupted result.");
                }
            }
            sentinel.Verify();
        }
        GC.KeepAlive(sentinel);
    }

    private static async Task CheckErrors(Engine engine, Component component)
    {
        foreach (var hostError in new[] { true, false })
        {
            using var store = new Store(engine);
            store.Fuel = ulong.MaxValue;
            using var linker = CreateLinker(engine, hostError);
            var instance = linker.Instantiate(store, component);
            try
            {
                if (hostError)
                {
                    await instance.GetFunction("run")!.CallAsync(ComponentValue.S32(1));
                }
                else
                {
                    await instance.GetFunction("boom")!.CallAsync();
                }
                throw new InvalidOperationException("Expected host error/guest trap was not propagated.");
            }
            catch (WasmtimeException error)
            {
                if (hostError && !error.Message.Contains("fiber-probe host failure"))
                {
                    throw;
                }
                if (hostError)
                {
                    ExpectCause<InvalidOperationException>(store, error, "fiber-probe host failure");
                }
            }
            using var freshStore = new Store(engine);
            freshStore.Fuel = ulong.MaxValue;
            var fresh = linker.Instantiate(freshStore, component);
            if ((await fresh.GetFunction("spin")!.CallAsync(ComponentValue.S32(100)))!.AsS32() != 42)
            {
                throw new InvalidOperationException("Store failed after a host error or guest trap.");
            }
            Interlocked.Increment(ref recoveredTraps);
        }
    }

    private static void EnableYield(Store store)
    {
        // Only async calls can yield; synchronous probe stores keep running to completion.
        if (store.IsComponentModelAsyncEnabled)
        {
            store.SetFuelAsyncYieldInterval(50);
        }
    }

    [DllImport("wasmtime")]
    private static extern ulong wasmtime_thread_fiber_started();

    [DllImport("wasmtime")]
    private static extern ulong wasmtime_thread_fiber_live();

    [DllImport("wasmtime")]
    private static extern ulong wasmtime_thread_fiber_tls_suspensions();

    private sealed class Sentinel
    {
        private readonly byte[] payload = Enumerable.Repeat((byte)0x5a, 1024).ToArray();
        internal int Finalized;

        ~Sentinel() => Interlocked.Exchange(ref Finalized, 1);

        internal void Verify()
        {
            if (Volatile.Read(ref Finalized) != 0 || payload.Any(value => value != 0x5a))
            {
                throw new InvalidOperationException("Premature finalization or corrupted sentinel contents.");
            }
        }
    }

    private sealed class PollThread : IDisposable
    {
        private readonly BlockingCollection<(IntPtr Future, TaskCompletionSource<bool> Result)> queue = new();
        private readonly Thread thread;
        internal int ThreadId => thread.ManagedThreadId;

        internal PollThread()
        {
            thread = new Thread(() =>
            {
                foreach (var request in queue.GetConsumingEnumerable())
                {
                    try
                    {
                        if (ThreadBackend || NativeCallbackBridge.Enabled)
                        {
                            pollingThreads.TryAdd(Environment.CurrentManagedThreadId, 0);
                        }
                        request.Result.SetResult(ComponentFunction.Native.wasmtime_call_future_poll(request.Future));
                    }
                    catch (Exception error)
                    {
                        request.Result.SetException(error);
                    }
                }
            });
            thread.Start();
        }

        internal bool Poll(IntPtr future)
        {
            var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.Add((future, result));
            return result.Task.GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            queue.CompleteAdding();
            thread.Join();
            queue.Dispose();
        }
    }

    private sealed class NativeCall : IDisposable
    {
        private readonly Store store;
        private readonly ComponentValueMarshaller.AllocationScope scope = new();
        private readonly IntPtr result;
        private readonly IntPtr error;
        private bool hasResult;
        internal IntPtr Future { get; private set; }

        internal NativeCall(Store store, ComponentInstance instance, string name, int argument)
        {
            this.store = store;
            try
            {
                using var export = instance.GetExport(name)!;
                if (!ComponentInstance.Native.wasmtime_component_instance_get_func(
                    in instance.instance, store.Context.handle, export.NativeHandle, out var function))
                {
                    throw new InvalidOperationException($"Missing probe function {name}.");
                }
                // All input/output addresses, including the function struct, outlive the future.
                var functionBuffer = scope.Allocate(Marshal.SizeOf<ComponentFunction.Native.Func>());
                Marshal.StructureToPtr(function, functionBuffer, false);
                var args = scope.Allocate(ComponentValueMarshaller.ValueSize);
                ComponentValueMarshaller.Write(ComponentValue.S32(argument), args, scope);
                result = scope.Allocate(ComponentValueMarshaller.ValueSize);
                error = scope.Allocate(IntPtr.Size);
                Future = wasmtime_component_func_call_async(
                    functionBuffer, store.Context.handle, args, 1, result, 1, error);
                if (Future == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Native future creation failed.");
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal int Complete()
        {
            ComponentFunction.Native.wasmtime_call_future_delete(Future);
            Future = IntPtr.Zero;
            var ownedError = Marshal.ReadIntPtr(error);
            if (ownedError != IntPtr.Zero)
            {
                Marshal.WriteIntPtr(error, IntPtr.Zero);
                throw WasmtimeException.FromOwnedError(ownedError);
            }
            hasResult = true;
            return ComponentValueMarshaller.Read(result).AsS32();
        }

        public void Dispose()
        {
            if (Future != IntPtr.Zero)
            {
                ComponentFunction.Native.wasmtime_call_future_delete(Future);
                Future = IntPtr.Zero;
            }
            if (hasResult)
            {
                ComponentValueNative.wasmtime_component_val_delete(result);
                hasResult = false;
            }
            if (error != IntPtr.Zero)
            {
                var ownedError = Marshal.ReadIntPtr(error);
                if (ownedError != IntPtr.Zero)
                {
                    ComponentFunction.Native.wasmtime_error_delete(ownedError);
                    Marshal.WriteIntPtr(error, IntPtr.Zero);
                }
            }
            scope.Dispose();
            GC.KeepAlive(store);
        }

        [DllImport("wasmtime")]
        private static extern IntPtr wasmtime_component_func_call_async(
            IntPtr function, IntPtr context, IntPtr args, nuint nargs, IntPtr results, nuint nresults, IntPtr error);
    }
}
