using System;
using System.Collections.Generic;
using System.Threading;
using Wasmtime.Components;

namespace Wasmtime
{
    public partial class Store
    {
        private const int ComponentOperationIdle = 0;
        private const int ComponentOperationRunning = 1;
        private const int ComponentOperationsClosed = 2;

        private readonly object componentResourcesGate = new();
        private readonly List<ComponentResource> pendingComponentDrops = new();
        private int componentOperationState;
        private int componentOperationThread;
        private int componentOperationDepth;
        private bool componentDisposeRequested;
        private HostResourceTable? componentResources;
        private ComponentHostResourceType[] componentHostResourceTypes = Array.Empty<ComponentHostResourceType>();
        private int maxComponentHostResources = int.MaxValue;
        private bool componentResourcesReleased;
        private List<Exception>? componentDisposeFailures;
        private bool componentDropsClosed;
        private bool componentPoisoned;
        private long leakedComponentHandles;

        /// <summary>
        /// Gets or sets the upper bound on host objects this Store's guests may own or borrow at once.
        /// </summary>
        /// <remarks>
        /// Defaults to <see cref="int.MaxValue"/>. Exceeding it fails the transfer: an exception before
        /// an export call, or a trap when a host callback returns the object.
        /// </remarks>
        /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
        public int MaxComponentHostResources
        {
            get => Volatile.Read(ref maxComponentHostResources);
            set
            {
                if (value < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value, "Expected a non-negative limit.");
                }

                Volatile.Write(ref maxComponentHostResources, value);
            }
        }

        /// <summary>
        /// Runs the resource drops deferred by <see cref="ComponentResource.Dispose"/>, on the calling thread, as one
        /// top-level component operation, and rethrows destructor failures that <see cref="ComponentResource.Dispose"/>
        /// recorded instead of throwing.
        /// </summary>
        /// <remarks>
        /// Deferred drops only ever run here and in <see cref="ReleaseComponentResources"/>, never inside an
        /// unrelated call.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// A component operation is in progress, or <see cref="ReleaseComponentResources"/> was called.
        /// </exception>
        /// <exception cref="ObjectDisposedException">The Store is disposed.</exception>
        /// <exception cref="AggregateException">One or more drops failed; all were attempted.</exception>
        public void FlushComponentResources()
        {
            List<Exception>? failures;
            BeginComponentOperation();
            try
            {
                failures = DropPendingComponentResources();
            }
            finally
            {
                EndComponentOperation();
            }

            ThrowComponentResourceFailures(failures, TakeComponentDisposeFailures(), null);
        }

        /// <summary>
        /// Ends component use of this Store: runs the drops deferred by <see cref="ComponentResource.Dispose"/>,
        /// then the destructor of every host object a guest still owns. Afterwards every component operation on
        /// this Store throws <see cref="InvalidOperationException"/>.
        /// </summary>
        /// <remarks>
        /// <see cref="Dispose"/> runs no destructors, because Wasmtime runs none when it deletes a Store.
        /// Call this just before <see cref="Dispose"/> when host objects owned by guests need deterministic
        /// cleanup. Calling it again does nothing.
        /// </remarks>
        /// <exception cref="InvalidOperationException">A component operation is in progress.</exception>
        /// <exception cref="ObjectDisposedException">The Store is disposed.</exception>
        /// <exception cref="AggregateException">
        /// One or more destructors or drops failed, or a failure recorded by <see cref="ComponentResource.Dispose"/>;
        /// all were attempted.
        /// </exception>
        public void ReleaseComponentResources()
        {
            if (Volatile.Read(ref componentResourcesReleased))
            {
                return;
            }

            List<KeyValuePair<ComponentHostResourceType, object>>? owned;
            List<Exception>? dropFailures;
            BeginComponentOperation();
            try
            {
                dropFailures = DropPendingComponentResources();
                Volatile.Write(ref componentResourcesReleased, true);
                foreach (var late in TakePendingComponentDrops(close: true))
                {
                    late.ReleaseQueued();
                }

                owned = Volatile.Read(ref componentResources)?.Drain();
            }
            finally
            {
                EndComponentOperation();
            }

            List<Exception>? destructorFailures = null;
            if (owned is not null)
            {
                foreach (var entry in owned)
                {
                    try
                    {
                        entry.Key.Destroy(entry.Value);
                    }
                    catch (Exception exception)
                    {
                        (destructorFailures ??= new List<Exception>()).Add(exception);
                    }
                }
            }

            ThrowComponentResourceFailures(dropFailures, TakeComponentDisposeFailures(), destructorFailures);
        }

        /// <summary>Gets a snapshot of this Store's component resource bookkeeping, for diagnostics and tests.</summary>
        /// <returns>The statistics.</returns>
        public ComponentResourceStatistics GetComponentResourceStatistics()
        {
            var table = Volatile.Read(ref componentResources);
            return new ComponentResourceStatistics(
                table?.GuestOwnedCount ?? 0,
                table?.LentCount ?? 0,
                PendingComponentDropCount,
                Interlocked.Read(ref leakedComponentHandles));
        }

        /// <summary>
        /// Starts a component operation (an export call, an instantiation, a resource drop or a flush) on the
        /// calling thread. Host callbacks run inside the operation that called into WebAssembly.
        /// </summary>
        /// <exception cref="ObjectDisposedException">The Store is disposed.</exception>
        /// <exception cref="InvalidOperationException">
        /// A component operation is already in progress, or the Store released its component resources.
        /// </exception>
        internal void BeginComponentOperation() => BeginComponentOperation(nested: false);

        /// <summary>
        /// Like <see cref="BeginComponentOperation()"/>. With <paramref name="nested"/>, an operation already in
        /// progress on the calling thread is joined instead of refused, so a host callback can call an export or
        /// instantiate a component in its own Store, as Wasmtime allows.
        /// </summary>
        internal void BeginComponentOperation(bool nested)
        {
            if (nested && Volatile.Read(ref componentOperationState) == ComponentOperationRunning &&
                Volatile.Read(ref componentOperationThread) == Environment.CurrentManagedThreadId)
            {
                if (Volatile.Read(ref componentResourcesReleased))
                {
                    throw new InvalidOperationException(
                        "ReleaseComponentResources was called on this store, so no further component operations are possible.");
                }

                componentOperationDepth++;
                return;
            }

            var state = Interlocked.CompareExchange(
                ref componentOperationState, ComponentOperationRunning, ComponentOperationIdle);
            if (state == ComponentOperationsClosed)
            {
                throw new ObjectDisposedException(typeof(Store).FullName);
            }

            if (state != ComponentOperationIdle)
            {
                throw new InvalidOperationException("A component operation is already in progress on this store.");
            }

            if (Volatile.Read(ref componentResourcesReleased))
            {
                Volatile.Write(ref componentOperationState, ComponentOperationIdle);
                throw new InvalidOperationException(
                    "ReleaseComponentResources was called on this store, so no further component operations are possible.");
            }

            EnterComponentOperation();
        }

        /// <summary>
        /// Like <see cref="BeginComponentOperation()"/>, but returns false instead of throwing when an operation is in
        /// progress, the Store is disposed, or its component resources were released.
        /// </summary>
        internal bool TryBeginComponentOperation()
        {
            if (Interlocked.CompareExchange(
                    ref componentOperationState, ComponentOperationRunning, ComponentOperationIdle) != ComponentOperationIdle)
            {
                return false;
            }

            if (Volatile.Read(ref componentResourcesReleased))
            {
                Volatile.Write(ref componentOperationState, ComponentOperationIdle);
                return false;
            }

            EnterComponentOperation();
            return true;
        }

        internal void EndComponentOperation()
        {
            if (--componentOperationDepth > 0)
            {
                return;
            }

            Volatile.Write(ref componentOperationThread, 0);
            Volatile.Write(ref componentOperationState, ComponentOperationIdle);

            // Dispose was called while the operation ran: finish it now that Wasmtime no longer uses the Store.
            if (Volatile.Read(ref componentDisposeRequested))
            {
                Dispose();
            }
        }

        private void EnterComponentOperation()
        {
            componentOperationDepth = 1;
            Volatile.Write(ref componentOperationThread, Environment.CurrentManagedThreadId);
        }

        /// <summary>
        /// Refuses further component operations because the Store is being disposed, and forgets its component
        /// resources without running destructors (Wasmtime runs none either when it deletes a Store).
        /// </summary>
        /// <returns>
        /// False when the Store was already disposed, or when a component operation is in progress; the end of that
        /// operation then disposes the Store.
        /// </returns>
        private bool CloseComponentOperations()
        {
            var state = Interlocked.CompareExchange(
                ref componentOperationState, ComponentOperationsClosed, ComponentOperationIdle);
            if (state == ComponentOperationRunning)
            {
                Volatile.Write(ref componentDisposeRequested, true);

                // The operation may have ended before it could see the request.
                state = Interlocked.CompareExchange(
                    ref componentOperationState, ComponentOperationsClosed, ComponentOperationIdle);
                if (state != ComponentOperationIdle)
                {
                    return false;
                }
            }

            if (state == ComponentOperationsClosed)
            {
                return false;
            }

            Volatile.Read(ref componentResources)?.Abandon();
            foreach (var pending in TakePendingComponentDrops(close: true))
            {
                pending.ReleaseQueued();
            }

            return true;
        }

        /// <summary>Whether a component operation of this Store is running on the calling thread.</summary>
        internal bool IsInComponentOperationOnCurrentThread =>
            Volatile.Read(ref componentOperationState) == ComponentOperationRunning &&
            Volatile.Read(ref componentOperationThread) == Environment.CurrentManagedThreadId;

        /// <summary>True once the Store was disposed or released its component resources.</summary>
        internal bool ComponentResourcesClosed =>
            Volatile.Read(ref componentOperationState) == ComponentOperationsClosed || handle.IsClosed ||
            Volatile.Read(ref componentResourcesReleased);

        /// <summary>
        /// True once a component call or resource drop failed natively. Disposing a native resource handle
        /// afterwards only frees its memory.
        /// </summary>
        internal bool IsComponentPoisoned => Volatile.Read(ref componentPoisoned);

        internal void MarkComponentPoisoned() => Volatile.Write(ref componentPoisoned, true);

        internal void RecordLeakedComponentHandle() => Interlocked.Increment(ref leakedComponentHandles);

        /// <summary>Keeps a failure of an immediate drop in Dispose for the next flush or release to throw.</summary>
        internal void RecordComponentDisposeFailure(Exception failure)
        {
            lock (componentResourcesGate)
            {
                (componentDisposeFailures ??= new List<Exception>()).Add(failure);
            }
        }

        /// <summary>Queues a handle for the next flush; after teardown the handle is released at once.</summary>
        internal void EnqueueComponentDrop(ComponentResource resource)
        {
            lock (componentResourcesGate)
            {
                if (!componentDropsClosed)
                {
                    pendingComponentDrops.Add(resource);
                    return;
                }
            }

            resource.ReleaseQueued();
        }

        /// <summary>The table of host objects this Store's guests own or borrow, created on first use.</summary>
        internal HostResourceTable ComponentResourceTable
        {
            get
            {
                var table = Volatile.Read(ref componentResources);
                if (table is not null)
                {
                    return table;
                }

                Interlocked.CompareExchange(ref componentResources, new HostResourceTable(this), null);
                return componentResources!;
            }
        }

        internal HostResourceTable? PeekComponentResourceTable() => Volatile.Read(ref componentResources);

        /// <summary>The binding host resource types of every linker this Store instantiated from.</summary>
        internal IReadOnlyList<ComponentHostResourceType> ComponentHostResourceTypes =>
            Volatile.Read(ref componentHostResourceTypes);

        internal void AddComponentHostResourceTypes(IReadOnlyList<ComponentHostResourceType> types)
        {
            lock (componentResourcesGate)
            {
                var current = componentHostResourceTypes;
                List<ComponentHostResourceType>? added = null;
                foreach (var type in types)
                {
                    if (Array.IndexOf(current, type) < 0 && (added is null || !added.Contains(type)))
                    {
                        (added ??= new List<ComponentHostResourceType>()).Add(type);
                    }
                }

                if (added is null)
                {
                    return;
                }

                var merged = new ComponentHostResourceType[current.Length + added.Count];
                current.CopyTo(merged, 0);
                added.CopyTo(merged, current.Length);
                Volatile.Write(ref componentHostResourceTypes, merged);
            }
        }

        /// <summary>Number of native resource handles waiting for a deferred drop.</summary>
        private int PendingComponentDropCount
        {
            get
            {
                lock (componentResourcesGate)
                {
                    var count = 0;
                    foreach (var pending in pendingComponentDrops)
                    {
                        if (pending.RawState == ComponentResourceState.DropPending)
                        {
                            count++;
                        }
                    }

                    return count;
                }
            }
        }

        /// <summary>Runs the queued drops with this Store's context, inside a component operation the caller began.</summary>
        private List<Exception>? DropPendingComponentResources()
        {
            List<Exception>? failures = null;
            foreach (var pending in TakePendingComponentDrops(close: false))
            {
                if (pending.DropQueued(contextHandle) is { } failure)
                {
                    (failures ??= new List<Exception>()).Add(failure);
                }
            }

            return failures;
        }

        private List<ComponentResource> TakePendingComponentDrops(bool close)
        {
            lock (componentResourcesGate)
            {
                componentDropsClosed |= close;
                var pending = new List<ComponentResource>(pendingComponentDrops);
                pendingComponentDrops.Clear();
                return pending;
            }
        }

        private List<Exception>? TakeComponentDisposeFailures()
        {
            lock (componentResourcesGate)
            {
                var failures = componentDisposeFailures;
                componentDisposeFailures = null;
                return failures;
            }
        }

        private static void ThrowComponentResourceFailures(
            List<Exception>? first,
            List<Exception>? second,
            List<Exception>? third)
        {
            var count = (first?.Count ?? 0) + (second?.Count ?? 0) + (third?.Count ?? 0);
            if (count == 0)
            {
                return;
            }

            var all = new List<Exception>(count);
            all.AddRange(first ?? (IEnumerable<Exception>)Array.Empty<Exception>());
            all.AddRange(second ?? (IEnumerable<Exception>)Array.Empty<Exception>());
            all.AddRange(third ?? (IEnumerable<Exception>)Array.Empty<Exception>());
            throw new AggregateException("One or more component resource drops or destructors failed.", all);
        }
    }
}
