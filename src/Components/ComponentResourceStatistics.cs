namespace Wasmtime.Components;

/// <summary>A snapshot of a <see cref="Store"/>'s component resource bookkeeping, for diagnostics and tests.</summary>
/// <remarks>Returned by <see cref="Store.GetComponentResourceStatistics"/>.</remarks>
public readonly struct ComponentResourceStatistics
{
    internal ComponentResourceStatistics(
        int hostObjectsHeldByGuests,
        int hostObjectsLent,
        int pendingDrops,
        long leakedHandles)
    {
        HostObjectsHeldByGuests = hostObjectsHeldByGuests;
        HostObjectsLent = hostObjectsLent;
        PendingDrops = pendingDrops;
        LeakedHandles = leakedHandles;
    }

    /// <summary>Gets the number of host objects guests of the store own, including transfers in progress.</summary>
    public int HostObjectsHeldByGuests { get; }

    /// <summary>Gets the number of host objects lent to guests (non-zero only during a call).</summary>
    public int HostObjectsLent { get; }

    /// <summary>
    /// Gets the number of native resource handles whose drop <see cref="ComponentResource.Dispose"/> deferred to
    /// the next <see cref="Store.FlushComponentResources"/>.
    /// </summary>
    public int PendingDrops { get; }

    /// <summary>
    /// Gets the number of owned native resource handles freed by a finalizer instead of being dropped,
    /// transferred or disposed. Their resources stay allocated in the Store until it is disposed.
    /// </summary>
    public long LeakedHandles { get; }
}
