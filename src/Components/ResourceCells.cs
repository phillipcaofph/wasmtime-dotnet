using System;

namespace Wasmtime.Components;

/// <summary>Where a resource cell came from, which decides what the user may do with it.</summary>
internal enum ResourceCellOrigin
{
    /// <summary>Built by user code with a <see cref="ComponentValue"/> factory.</summary>
    Value,

    /// <summary>A host callback argument: a view that expires when the callback ends.</summary>
    Argument,

    /// <summary>An export call result: the host owns it outright.</summary>
    Result,
}

/// <summary>
/// The payload of a <see cref="ComponentValueKind.Resource"/> value: a binding host object
/// (<see cref="HostResourceCell"/>) or a native handle (<see cref="NativeResourceCell"/>).
/// </summary>
/// <remarks>
/// Cells read from Wasmtime are filled in before any <see cref="ComponentValue"/> reaches user code,
/// so values stay immutable from the user's point of view.
/// </remarks>
internal abstract class ResourceCell
{
    private bool expired;

    private protected ResourceCell(ComponentResourceOwnership ownership, ResourceCellOrigin origin)
    {
        Ownership = ownership;
        Origin = origin;
    }

    /// <summary>Own/Borrow when stated or read from Wasmtime; None when inferred from the position.</summary>
    public ComponentResourceOwnership Ownership { get; }

    public ResourceCellOrigin Origin { get; }

    public abstract ComponentResourceType Type { get; }

    /// <summary>True once a callback-argument view has outlived its callback.</summary>
    public bool Expired => expired;

    public void Expire() => expired = true;

    /// <summary>The type name used by <see cref="ComponentValue.ToString"/>.</summary>
    public abstract string DescribeType();

    protected void ThrowIfExpired()
    {
        if (expired)
        {
            throw new ObjectDisposedException(
                nameof(ComponentValue),
                "This resource was a host callback argument and the callback has ended, so it can no longer be used.");
        }
    }
}

/// <summary>A resource of a binding host type: a managed object plus its type.</summary>
internal sealed class HostResourceCell : ResourceCell
{
    private object? value;
    private bool claimed;
    private bool transferred;

    public HostResourceCell(
        ComponentHostResourceType type,
        ComponentResourceOwnership ownership,
        ResourceCellOrigin origin,
        object? value)
        : base(ownership, origin)
    {
        HostType = type;
        this.value = value;
    }

    public ComponentHostResourceType HostType { get; }

    public override ComponentResourceType Type => HostType;

    /// <summary>True when the object is available (callback arguments are filled after conversion).</summary>
    public bool IsResolved => value is not null;

    /// <summary>True when a callback argument was claimed with TakeHostResource.</summary>
    public bool Claimed => claimed;

    /// <summary>
    /// True while an own callback argument has been moved into a guest by an export call made from inside
    /// the callback (for example an export of another Store). The callback then no longer releases it.
    /// </summary>
    public bool Transferred => transferred;

    /// <summary>The object, or null before resolution. Never throws; for bookkeeping only.</summary>
    public object? RawValue => value;

    /// <summary>The object for a user accessor or a transfer; throws for an expired view.</summary>
    public object Value
    {
        get
        {
            ThrowIfExpired();
            return value ?? throw new InvalidOperationException("The resource argument has not been converted yet.");
        }
    }

    public void Resolve(object resolved) => value = resolved;

    /// <summary>Claims the object of an owned argument or result for the host.</summary>
    public object Claim()
    {
        ThrowIfExpired();
        if (Ownership != ComponentResourceOwnership.Own || Origin == ResourceCellOrigin.Value)
        {
            throw new InvalidOperationException(
                "Only an owned resource received from a component (an own callback argument or an export result) can be taken.");
        }

        if (claimed)
        {
            throw new InvalidOperationException("This resource has already been taken.");
        }

        if (transferred)
        {
            throw new InvalidOperationException("This resource has been moved into a component, so it can no longer be taken.");
        }

        claimed = true;
        return Value;
    }

    /// <summary>Records (or, when the call was never issued, undoes) a move of an own argument into a guest.</summary>
    public void SetTransferred(bool value) => transferred = value;

    public override string DescribeType() => HostType.DisplayName;
}

/// <summary>A resource of a type not created by this binding, carried by a <see cref="ComponentResource"/>.</summary>
internal sealed class NativeResourceCell : ResourceCell
{
    private readonly ComponentResource handle;
    private bool claimed;

    public NativeResourceCell(ComponentResource handle, ComponentResourceOwnership ownership, ResourceCellOrigin origin)
        : base(ownership, origin)
    {
        this.handle = handle;
    }

    /// <summary>The handle, never throwing; for bookkeeping only.</summary>
    public ComponentResource RawHandle => handle;

    /// <summary>The handle for a user accessor or a transfer; throws for an expired callback-argument view.</summary>
    public ComponentResource Handle
    {
        get
        {
            ThrowIfExpired();
            return handle;
        }
    }

    public override ComponentResourceType Type => handle.Type;

    /// <summary>True when an own callback argument was claimed with TakeResource.</summary>
    public bool Claimed => claimed;

    /// <summary>
    /// True when the caller owns the handle: a value built by user code with an owned handle, an export
    /// result, or a claimed callback argument.
    /// </summary>
    public bool OwnedByCaller =>
        handle.IsOwned && (Origin != ResourceCellOrigin.Argument || claimed);

    /// <summary>Claims the handle of an owned callback argument, or returns the handle of an export result.</summary>
    public ComponentResource Claim()
    {
        ThrowIfExpired();
        if (Ownership != ComponentResourceOwnership.Own || Origin == ResourceCellOrigin.Value)
        {
            throw new InvalidOperationException(
                "Only an owned resource received from a component (an own callback argument or an export result) can be taken.");
        }

        if (Origin == ResourceCellOrigin.Result)
        {
            return handle;
        }

        if (claimed)
        {
            throw new InvalidOperationException("This resource has already been taken.");
        }

        claimed = true;
        return handle;
    }

    public override string DescribeType() => handle.Type.ToString();
}
