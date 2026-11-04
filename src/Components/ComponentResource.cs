using System;
using System.Threading;

namespace Wasmtime.Components;

/// <summary>The lifecycle state of a <see cref="ComponentResource"/>.</summary>
public enum ComponentResourceState
{
    /// <summary>Usable.</summary>
    Live,

    /// <summary>Ownership moved into a component (an <c>own</c> parameter or an <c>own</c> callback result).</summary>
    Transferred,

    /// <summary>
    /// The host gave the handle up by dropping it. The destructor (if any) ran, or trapped; or, after a component
    /// call or drop failed on the Store, it was skipped and only the native memory was freed (the resource then
    /// stays allocated in the Store until the Store is disposed).
    /// </summary>
    Dropped,

    /// <summary>
    /// <see cref="ComponentResource.Dispose"/> was called while a drop was not possible; the drop runs at the
    /// next <see cref="Store.FlushComponentResources"/>.
    /// </summary>
    DropPending,

    /// <summary>A host callback argument view whose callback has ended.</summary>
    Expired,

    /// <summary>
    /// The owning Store was disposed or released its component resources; only native memory remained.
    /// </summary>
    Released,
}

/// <summary>
/// Owns one <c>wasmtime_component_resource_any_t*</c> box and frees it exactly once.
/// </summary>
/// <remarks>
/// Not finalizable: the <see cref="ComponentResource"/> holding it frees it from its own finalizer, so a
/// box queued for a deferred drop is never freed behind the Store's back.
/// </remarks>
internal sealed class ResourceBox
{
    private IntPtr pointer;

    public ResourceBox(IntPtr pointer) => this.pointer = pointer;

    /// <summary>The box, or zero once freed.</summary>
    public IntPtr Pointer => Volatile.Read(ref pointer);

    public bool IsFreed => Pointer == IntPtr.Zero;

    /// <summary>Frees the box; later calls do nothing. Touches no Store.</summary>
    public void Free()
    {
        var current = Interlocked.Exchange(ref pointer, IntPtr.Zero);
        if (current != IntPtr.Zero)
        {
            ComponentResourceNative.wasmtime_component_resource_any_delete(current);
        }
    }
}

/// <summary>
/// A handle to a resource whose type was not created by this binding: a guest-defined resource returned
/// by an export, or a host resource defined elsewhere (WASI, <see cref="ComponentLinker.DefineUnknownImportsAsTraps(Component)"/>).
/// It owns exactly one native box.
/// </summary>
/// <remarks>
/// <para>
/// An owned handle must end in exactly one of: passed to an <c>own</c> parameter or returned as an
/// <c>own</c> callback result (<see cref="ComponentResourceState.Transferred"/>), <see cref="Drop"/>
/// (<see cref="ComponentResourceState.Dropped"/>), or the Store's teardown
/// (<see cref="ComponentResourceState.Released"/>). <see cref="Dispose"/> drops when that is safe and
/// otherwise defers the drop to <see cref="Store.FlushComponentResources"/>; it never throws for state reasons.
/// </para>
/// <para>
/// A finalized handle frees only its native box: the resource stays allocated in the Store and its
/// destructor never runs (counted in <see cref="ComponentResourceStatistics.LeakedHandles"/>).
/// </para>
/// <para>
/// A handle is bound to the Store that produced it; using it with another Store throws before any native call.
/// A borrowed handle received by a host callback is an opaque view: it only reports its
/// <see cref="Type"/> and <see cref="State"/>, cannot be passed anywhere, and expires when the callback ends.
/// </para>
/// </remarks>
public sealed class ComponentResource : IDisposable
{
    private const int LiveState = (int)ComponentResourceState.Live;
    private const int TransferredState = (int)ComponentResourceState.Transferred;
    private const int DroppedState = (int)ComponentResourceState.Dropped;
    private const int DropPendingState = (int)ComponentResourceState.DropPending;
    private const int ExpiredState = (int)ComponentResourceState.Expired;
    private const int ReleasedState = (int)ComponentResourceState.Released;

    private readonly ResourceBox? box;
    private int state;
    private int uses;

    private ComponentResource(Store store, ComponentResourceType type, ResourceBox? box)
    {
        Store = store;
        Type = type;
        this.box = box;
        if (box is null)
        {
            GC.SuppressFinalize(this);
        }
    }

    /// <summary>Frees the native box of a handle nobody disposed. Runs no guest code and touches no Store.</summary>
    ~ComponentResource()
    {
        if (box is null || box.IsFreed)
        {
            return;
        }

        if (Volatile.Read(ref state) == LiveState && !Store.ComponentResourcesClosed)
        {
            Store.RecordLeakedComponentHandle();
        }

        box.Free();
    }

    /// <summary>Gets the Store this handle belongs to.</summary>
    public Store Store { get; }

    /// <summary>Gets the resource type.</summary>
    public ComponentResourceType Type { get; }

    /// <summary>
    /// Gets whether this handle owns the resource; false for a borrow view received by a host callback.
    /// </summary>
    public bool IsOwned => box is not null;

    /// <summary>Gets the current state.</summary>
    public ComponentResourceState State
    {
        get
        {
            var current = Volatile.Read(ref state);
            if ((current == LiveState || current == DropPendingState) && Store.ComponentResourcesClosed)
            {
                return ComponentResourceState.Released;
            }

            return (ComponentResourceState)current;
        }
    }

    /// <summary>The native box while the handle holds one, for marshalling. Never zero for a usable handle.</summary>
    internal IntPtr BoxPointer => box?.Pointer ?? IntPtr.Zero;

    /// <summary>True while an export call is using this handle as an argument.</summary>
    internal bool InUse => Volatile.Read(ref uses) != 0;

    internal ComponentResourceState RawState => (ComponentResourceState)Volatile.Read(ref state);

    /// <summary>
    /// Creates an owned handle that takes ownership of <paramref name="any"/>, an owned
    /// <c>wasmtime_component_resource_any_t*</c>. Pure: it touches no Store.
    /// </summary>
    internal static ComponentResource CreateOwned(Store store, IntPtr any)
    {
        var resourceBox = new ResourceBox(any);
        try
        {
            return new ComponentResource(store, ReadType(any), resourceBox);
        }
        catch
        {
            resourceBox.Free();
            throw;
        }
    }

    /// <summary>
    /// Creates the opaque view of a borrow received by a host callback. <paramref name="any"/> stays owned
    /// by Wasmtime; only its type is read.
    /// </summary>
    internal static ComponentResource CreateBorrowView(Store store, IntPtr any) =>
        new(store, ReadType(any), null);

    private static ComponentResourceType ReadType(IntPtr any) =>
        new NativeResourceType(new SafeResourceTypeHandle(ComponentResourceNative.wasmtime_component_resource_any_type(any)));

    /// <summary>Drops the resource now, on the calling thread, as a top-level component operation.</summary>
    /// <remarks>Dropping a guest-defined resource runs its destructor in the guest.</remarks>
    /// <exception cref="ObjectDisposedException">The handle is not <see cref="ComponentResourceState.Live"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A borrow view, or a component operation is in progress on the Store (including inside a host callback).
    /// </exception>
    /// <exception cref="WasmtimeException">
    /// The destructor trapped. The handle is <see cref="ComponentResourceState.Dropped"/> and the Store's
    /// component instances should be considered unusable.
    /// </exception>
    public void Drop()
    {
        ThrowIfNotDroppable();
        Store.BeginComponentOperation();
        try
        {
            if (Interlocked.CompareExchange(ref state, DroppedState, LiveState) != LiveState)
            {
                throw NotLive();
            }

            if (DropNative(Store.Context.handle) is { } failure)
            {
                throw failure;
            }
        }
        finally
        {
            Store.EndComponentOperation();
            GC.KeepAlive(Store);
        }
    }

    /// <summary>
    /// Drops the resource now when that is safe; otherwise marks it <see cref="ComponentResourceState.DropPending"/>
    /// for <see cref="Store.FlushComponentResources"/>. A handle that is not live only frees its native memory.
    /// </summary>
    /// <remarks>
    /// Never throws for state reasons. A destructor trap during an immediate drop is recorded on the
    /// Store and rethrown by the next <see cref="Store.FlushComponentResources"/> or
    /// <see cref="Store.ReleaseComponentResources"/>, never by an unrelated call. The drop is deferred while a
    /// component operation is in progress (for example inside a host callback). After a component call or drop
    /// failed on the Store, only the native memory is freed (the handle becomes
    /// <see cref="ComponentResourceState.Dropped"/> without running the destructor). Safe to call concurrently
    /// with a drop or a call using the handle: memory a drop or a call is still reading is never freed here.
    /// </remarks>
    public void Dispose()
    {
        if (box is null)
        {
            return;
        }

        var current = Volatile.Read(ref state);
        if (current != LiveState)
        {
            // Only the code that moved the handle to Dropped frees its box (a drop may still be reading it), and a
            // DropPending box belongs to the queue. A spent handle's box can go once no export call is using it.
            if ((current == TransferredState || current == ExpiredState || current == ReleasedState) && !InUse)
            {
                box.Free();
            }

            return;
        }

        if (Store.ComponentResourcesClosed)
        {
            Release(LiveState);
            return;
        }

        if (!Store.TryBeginComponentOperation())
        {
            if (Store.ComponentResourcesClosed)
            {
                Release(LiveState);
            }
            else
            {
                Defer();
            }

            return;
        }

        try
        {
            if (Interlocked.CompareExchange(ref state, DroppedState, LiveState) != LiveState)
            {
                return;
            }

            if (Store.IsComponentPoisoned)
            {
                // Inside the component operation no export call can be using the box as an argument.
                box.Free();
            }
            else if (DropNative(Store.Context.handle) is { } failure)
            {
                Store.RecordComponentDisposeFailure(failure);
            }
        }
        finally
        {
            Store.EndComponentOperation();
            GC.KeepAlive(Store);
        }
    }

    /// <inheritdoc/>
    public override string ToString() => $"{(IsOwned ? "own" : "borrow")}<{Type}> ({State})";

    /// <summary>
    /// Marks an export-call argument as in use, so it is neither freed nor reused until <see cref="ReleaseUse"/>.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The handle can no longer be used.</exception>
    internal void AcquireUse()
    {
        Interlocked.Increment(ref uses);
        var current = Volatile.Read(ref state);
        if ((current != LiveState && current != DropPendingState) || BoxPointer == IntPtr.Zero)
        {
            Interlocked.Decrement(ref uses);
            throw NotLive();
        }
    }

    internal void ReleaseUse() => Interlocked.Decrement(ref uses);

    /// <summary>
    /// Records that ownership moved into a component (an issued call with this handle as an <c>own</c>
    /// argument, or a committed callback result) and frees the box.
    /// </summary>
    internal void CompleteTransfer()
    {
        while (true)
        {
            var current = Volatile.Read(ref state);
            if (current != LiveState && current != DropPendingState)
            {
                break;
            }

            if (Interlocked.CompareExchange(ref state, TransferredState, current) == current)
            {
                break;
            }
        }

        if (!InUse)
        {
            box?.Free();
        }
    }

    /// <summary>
    /// Starts moving a live owned handle into a callback's results: the handle reads
    /// <see cref="ComponentResourceState.Transferred"/> from now on, so it can no longer be passed, dropped or
    /// disposed, and its box is kept (marked in use) until <see cref="FinishResultTransfer"/> or
    /// <see cref="CancelResultTransfer"/>. Closes the window in which Wasmtime may already have consumed the
    /// clone in the results while the handle still read Live.
    /// </summary>
    /// <returns>False when the handle was not live.</returns>
    internal bool BeginResultTransfer()
    {
        Interlocked.Increment(ref uses);
        if (BoxPointer == IntPtr.Zero ||
            Interlocked.CompareExchange(ref state, TransferredState, LiveState) != LiveState)
        {
            Interlocked.Decrement(ref uses);
            return false;
        }

        return true;
    }

    /// <summary>The results carrying the handle were delivered: frees the box once nothing uses it.</summary>
    internal void FinishResultTransfer()
    {
        if (Interlocked.Decrement(ref uses) == 0)
        {
            box?.Free();
        }
    }

    /// <summary>The results carrying the handle were not delivered: the handle is the host's again.</summary>
    internal void CancelResultTransfer()
    {
        Interlocked.CompareExchange(ref state, LiveState, TransferredState);
        Interlocked.Decrement(ref uses);
    }

    /// <summary>Ends a callback-argument borrow view.</summary>
    internal void Expire() => Interlocked.CompareExchange(ref state, ExpiredState, LiveState);

    /// <summary>
    /// Runs a deferred drop with a context the caller legally holds (top-level, inside a component operation).
    /// </summary>
    /// <param name="context">The Store context.</param>
    /// <returns>The failure, or null.</returns>
    internal Exception? DropQueued(IntPtr context)
    {
        if (Interlocked.CompareExchange(ref state, DroppedState, DropPendingState) != DropPendingState)
        {
            return null;
        }

        return DropNative(context);
    }

    /// <summary>The Store is going away: frees the box of a queued handle without dropping it.</summary>
    internal void ReleaseQueued() => Release(DropPendingState);

    private void Release(int from)
    {
        if (Interlocked.CompareExchange(ref state, ReleasedState, from) == from)
        {
            box?.Free();
        }
    }

    private void Defer()
    {
        if (Interlocked.CompareExchange(ref state, DropPendingState, LiveState) == LiveState)
        {
            Store.EnqueueComponentDrop(this);
        }
    }

    /// <summary>
    /// Runs <c>wasmtime_component_resource_any_drop</c> after the caller moved the state to Dropped, and frees
    /// the box. A failure (a trapping destructor) marks the Store failed.
    /// </summary>
    /// <returns>The failure, or null.</returns>
    private Exception? DropNative(IntPtr context)
    {
        var error = ComponentResourceNative.wasmtime_component_resource_any_drop(context, box!.Pointer);
        box.Free();
        if (error == IntPtr.Zero)
        {
            return null;
        }

        Store.MarkComponentPoisoned();
        return WasmtimeException.FromOwnedError(error);
    }

    private void ThrowIfNotDroppable()
    {
        if (box is null)
        {
            if (State == ComponentResourceState.Expired)
            {
                throw NotLive();
            }

            throw new InvalidOperationException(
                "A borrowed resource received by a host callback cannot be dropped; the component that lent it owns it.");
        }

        if (State != ComponentResourceState.Live)
        {
            throw NotLive();
        }
    }

    private ObjectDisposedException NotLive() =>
        new(nameof(ComponentResource), $"The resource is {State} and can no longer be used.");

    /// <summary>
    /// Throws when this handle cannot be passed to a component: <see cref="InvalidOperationException"/> once
    /// transferred, <see cref="ObjectDisposedException"/> for every other state but Live.
    /// </summary>
    internal void ThrowIfNotUsable(string path)
    {
        var current = State;
        if (current == ComponentResourceState.Live)
        {
            return;
        }

        if (current == ComponentResourceState.Transferred)
        {
            throw new InvalidOperationException(
                $"{path}: the resource was moved into a component and can no longer be used by the host.");
        }

        throw new ObjectDisposedException(
            nameof(ComponentResource), $"{path}: the resource is {current} and can no longer be used.");
    }
}
