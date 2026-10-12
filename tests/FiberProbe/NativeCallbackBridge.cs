using System;
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

        if (Environment.GetEnvironmentVariable("WASMTIME_BRIDGE_SPIN_NS") is { } spin)
        {
            HostCallbackDispatcher.SpinNanoseconds = ulong.Parse(spin);
        }
    }

    internal static bool Enabled =>
        Environment.GetEnvironmentVariable("WASMTIME_CALLBACK_BRIDGE_EXPERIMENT") == "1";

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
    internal static void CollectGuest(Store store) => store.GC();

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

}
