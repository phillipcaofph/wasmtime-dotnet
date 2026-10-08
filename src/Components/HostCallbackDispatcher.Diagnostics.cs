using System.Runtime.InteropServices;

namespace Wasmtime.Components;

/// <summary>Native counters and shutdown, used by the fiber stress probes.</summary>
internal static unsafe partial class HostCallbackDispatcher
{
    internal static ulong Counter(Counters counter) => initialized ? bridge_counter((int)counter) : 0;

    /// <summary>Waits for queued work, then joins every worker. Must not be called from a worker.</summary>
    internal static void Shutdown()
    {
        if (initialized)
        {
            bridge_shutdown();
        }
    }

    /// <summary>Native counters. Values match <c>bridge_counter</c>.</summary>
    internal enum Counters
    {
        LiveRegistrations, CompletedRequests, PeakBusyWorkers, Exhaustions, OwnerOperations, LiveAsync,
        PeakLiveAsync, AsyncCancelled, StagedDeleted, LateFailures, ThreadsStarted, PoisonedResults,
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bridge_shutdown();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong bridge_counter(int which);
}
