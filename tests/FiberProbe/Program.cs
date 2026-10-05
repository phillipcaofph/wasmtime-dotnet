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

            if (scenario == "stack-overflow")
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
                var workers = scenario is "concurrent" or "concurrent-baseline" or "sync-control" or "guest-gc" ? 4 : 1;
                using var barrier = new Barrier(workers);
                await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Task.Run(() =>
                {
                    using var store = new Store(engine);
                    store.Fuel = ulong.MaxValue;
                    using var linker = CreateLinker(engine, false, scenario == "guest-gc" ? store : null);
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

            if (scenario is not ("errors" or "sync-control" or "stack-overflow") && pendingPolls == 0)
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
    }

    private static ComponentLinker CreateLinker(Engine engine, bool throws, Store? collectGuest = null)
    {
        var linker = new ComponentLinker(engine);
        using var root = linker.Root();
        root.DefineFunction("observe", (arguments, results) =>
        {
            if (throws)
            {
                throw new InvalidOperationException("fiber-probe host failure");
            }

            CheckManagedCallback();
            if (collectGuest is not null)
            {
                collectGuest.GC();
                Interlocked.Increment(ref guestCollections);
            }
            results[0] = ComponentValue.S32(arguments[0].AsS32());
        });
        return linker;
    }

    private static void CheckManagedCallback()
    {
        Interlocked.Increment(ref callbacks);
        if (ThreadBackend)
        {
            callbackThreads.TryAdd(Environment.CurrentManagedThreadId, 0);
        }
        CheckRoots();
    }

    private static async Task CheckStackOverflow(Engine engine, int iterations)
    {
        using var recursive = Component.FromText(engine, """
            (component
              (core module $m
                (func $recurse (export "recurse")
                  call $recurse
                  nop))
              (core instance $i (instantiate $m))
              (func (export "recurse") (canon lift (core func $i "recurse"))))
            """);
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
                root.DefineFunction("observe", (arguments, results) =>
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
        var error = wasmtime_context_fuel_async_yield_interval(store.Context.handle, 50);
        if (error != IntPtr.Zero)
        {
            throw WasmtimeException.FromOwnedError(error);
        }
    }

    [DllImport("wasmtime")]
    private static extern IntPtr wasmtime_context_fuel_async_yield_interval(IntPtr context, ulong interval);

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
                        if (ThreadBackend)
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
