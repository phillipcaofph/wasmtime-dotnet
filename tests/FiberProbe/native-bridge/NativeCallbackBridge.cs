using System;
using System.Runtime.InteropServices;
using System.Threading;
using Wasmtime.Components;

namespace Wasmtime.FiberProbe;

/// <summary>
/// Probe facade over the library's host callback isolation. Environment variables select
/// the mode so the stress tests can run the same scenarios with and without isolation.
/// </summary>
internal static class NativeCallbackBridge
{
    static NativeCallbackBridge()
    {
        if (!Enabled)
        {
            return;
        }

        if (Environment.GetEnvironmentVariable("WASMTIME_BRIDGE_MAX_WORKERS") is { } workers)
        {
            HostCallbackIsolation.MaxWorkerThreads = int.Parse(workers);
        }

        // 0 measures the library's fixed 1 ms repolling instead of completion signals.
        HostCallbackDispatcher.UseProgressSignals =
            Environment.GetEnvironmentVariable("WASMTIME_BRIDGE_WAKER") != "0";
        if (Environment.GetEnvironmentVariable("WASMTIME_BRIDGE_SPIN_NS") is { } spin)
        {
            HostCallbackDispatcher.SpinNanoseconds = ulong.Parse(spin);
        }
    }

    internal static bool Enabled =>
        Environment.GetEnvironmentVariable("WASMTIME_CALLBACK_BRIDGE_EXPERIMENT") == "1";

    /// <summary>Demonstrates the hazard the owner-call proxy fixes. Never enable in tests.</summary>
    private static bool UnsafeDirectStoreAccess =>
        Environment.GetEnvironmentVariable("WASMTIME_BRIDGE_UNSAFE_DIRECT_STORE_ACCESS") == "1";

    internal static int MaxWorkers => HostCallbackIsolation.MaxWorkerThreads;

    internal static void DefineFunction(
        ComponentLinkerInstance root, string name, ComponentFunctionCallback callback)
    {
        if (Enabled)
        {
            root.DefineIsolatedFunction(name, callback);
        }
        else
        {
            root.DefineDirectFunction(name, callback);
        }
    }

    internal static void DefineAsyncFunction(
        ComponentLinkerInstance root, string name, ComponentAsyncFunctionCallback callback)
    {
        if (!Enabled)
        {
            throw new InvalidOperationException("Async host imports require the callback bridge experiment.");
        }

        root.DefineAsyncFunction(name, callback);
    }

    /// <summary>
    /// Runs Store garbage collection. From an isolated callback for the same Store the library
    /// runs the collection on the Wasmtime activation that owns it.
    /// </summary>
    internal static void CollectGuest(Store store)
    {
        if (!UnsafeDirectStoreAccess || !Enabled)
        {
            store.GC();
            return;
        }

        // Bypasses both the owner-call proxy and the Store guard to show the cross-thread GC hazard.
        var serving = ComponentCallbackHooks.ServingContext;
        var previous = serving?.Value ?? IntPtr.Zero;
        if (serving is not null)
        {
            serving.Value = IntPtr.Zero;
        }

        try
        {
            var error = wasmtime_context_gc(store.ContextHandleUnchecked);
            if (error != IntPtr.Zero)
            {
                throw WasmtimeException.FromOwnedError(error);
            }

            GC.KeepAlive(store);
        }
        finally
        {
            if (serving is not null)
            {
                serving.Value = previous;
            }
        }
    }

    internal static ulong GetFuel(Store store) => store.Fuel;

    internal static void SetFuel(Store store, ulong fuel) => store.Fuel = fuel;

    internal static ulong Counter(BridgeCounter counter) =>
        HostCallbackDispatcher.Counter((HostCallbackDispatcher.Counters)counter);

    /// <summary>Host failures recorded but not yet consumed by a component call.</summary>
    internal static int PendingCauses => HostCallbackDispatcher.PendingCauses;

    internal static void Finish()
    {
        if (!Enabled)
        {
            return;
        }

        // Registration finalizers and cancellation notices are asynchronous jobs.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (Counter(BridgeCounter.LiveRegistrations) != 0 || Counter(BridgeCounter.LiveAsync) != 0)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new InvalidOperationException(
                    $"Callback bridge leaked: registrations={Counter(BridgeCounter.LiveRegistrations)}, " +
                    $"async={Counter(BridgeCounter.LiveAsync)}.");
            }

            Thread.Sleep(10);
        }

        HostCallbackDispatcher.Shutdown();
    }

    internal enum BridgeCounter
    {
        LiveRegistrations, CompletedRequests, PeakBusyWorkers, Exhaustions, OwnerOperations, LiveAsync,
        PeakLiveAsync, AsyncCancelled, StagedDeleted, LateFailures, ThreadsStarted, PoisonedResults,
    }

    [DllImport("wasmtime", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr wasmtime_context_gc(IntPtr context);
}
