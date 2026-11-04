using System;
using System.Diagnostics.CodeAnalysis;

namespace Wasmtime.Components;

/// <summary>Whether a resource value is an <c>own</c> or a <c>borrow</c> handle.</summary>
public enum ComponentResourceOwnership
{
    /// <summary>Not a resource, or a resource whose ownership is taken from the position it is written to.</summary>
    None = 0,

    /// <summary>An owned handle: writing it moves the resource.</summary>
    Own = 1,

    /// <summary>A borrowed handle: writing it lends the resource for one call.</summary>
    Borrow = 2,
}

public sealed partial class ComponentValue
{
    /// <summary>The resource payload of a <see cref="ComponentValueKind.Resource"/> value.</summary>
    internal ResourceCell? ResourceCell { get; private set; }

    /// <summary>
    /// Creates a resource value carrying a host object. Whether the object is moved (own) or lent
    /// (borrow) is taken from the parameter or result it is written to; a callback result always
    /// moves it to the guest.
    /// </summary>
    /// <typeparam name="T">The type of the host object.</typeparam>
    /// <param name="type">The resource type.</param>
    /// <param name="value">The host object.</param>
    /// <returns>The component value.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static ComponentValue Resource<T>(ComponentHostResourceType<T> type, T value) where T : class =>
        HostResource(type, value, ComponentResourceOwnership.None);

    /// <summary>
    /// Creates a resource value that moves a host object to the guest. It must be written to an
    /// <c>own</c> position; anything else throws before the native call.
    /// </summary>
    /// <typeparam name="T">The type of the host object.</typeparam>
    /// <param name="type">The resource type.</param>
    /// <param name="value">The host object.</param>
    /// <returns>The component value.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static ComponentValue Own<T>(ComponentHostResourceType<T> type, T value) where T : class =>
        HostResource(type, value, ComponentResourceOwnership.Own);

    /// <summary>
    /// Creates a resource value that lends a host object to the guest for one export call. It must be
    /// written to a <c>borrow</c> position; anything else throws before the native call.
    /// </summary>
    /// <typeparam name="T">The type of the host object.</typeparam>
    /// <param name="type">The resource type.</param>
    /// <param name="value">The host object.</param>
    /// <returns>The component value.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static ComponentValue Borrow<T>(ComponentHostResourceType<T> type, T value) where T : class =>
        HostResource(type, value, ComponentResourceOwnership.Borrow);

    private static ComponentValue HostResource<T>(
        ComponentHostResourceType<T> type,
        T value,
        ComponentResourceOwnership ownership) where T : class
    {
        if (type is null)
        {
            throw new ArgumentNullException(nameof(type));
        }

        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        return FromResourceCell(new HostResourceCell(type, ownership, ResourceCellOrigin.Value, value));
    }

    /// <summary>
    /// Creates a resource value carrying a native handle. Whether the resource is moved (own) or lent
    /// (borrow) is taken from the parameter or result it is written to; a callback result always moves it.
    /// </summary>
    /// <param name="resource">The handle.</param>
    /// <returns>The component value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="resource"/> is null.</exception>
    public static ComponentValue Resource(ComponentResource resource) =>
        NativeResource(resource, ComponentResourceOwnership.None);

    /// <summary>
    /// Creates a resource value that moves a native handle into a component. It must be written to an
    /// <c>own</c> position; anything else throws before the native call.
    /// </summary>
    /// <param name="resource">The handle.</param>
    /// <returns>The component value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="resource"/> is null.</exception>
    public static ComponentValue Own(ComponentResource resource) =>
        NativeResource(resource, ComponentResourceOwnership.Own);

    /// <summary>
    /// Creates a resource value that lends a native handle to a component for one export call. It must be
    /// written to a <c>borrow</c> position; anything else throws before the native call.
    /// </summary>
    /// <param name="resource">The handle.</param>
    /// <returns>The component value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="resource"/> is null.</exception>
    public static ComponentValue Borrow(ComponentResource resource) =>
        NativeResource(resource, ComponentResourceOwnership.Borrow);

    private static ComponentValue NativeResource(ComponentResource resource, ComponentResourceOwnership ownership)
    {
        if (resource is null)
        {
            throw new ArgumentNullException(nameof(resource));
        }

        return FromResourceCell(new NativeResourceCell(resource, ownership, ResourceCellOrigin.Value));
    }

    internal static ComponentValue FromResourceCell(ResourceCell cell) =>
        new(ComponentValueKind.Resource) { ResourceCell = cell };

    /// <summary>
    /// Gets <see cref="ComponentResourceOwnership.Own"/> or <see cref="ComponentResourceOwnership.Borrow"/>
    /// for resources read from Wasmtime or built with <see cref="Own{T}"/> or <see cref="Borrow{T}"/>, and
    /// <see cref="ComponentResourceOwnership.None"/> for resources whose ownership is inferred and for
    /// values of other kinds. Never throws.
    /// </summary>
    public ComponentResourceOwnership ResourceOwnership => ResourceCell?.Ownership ?? ComponentResourceOwnership.None;

    /// <summary>Gets the resource type of this value.</summary>
    /// <returns>The resource type.</returns>
    /// <exception cref="InvalidOperationException">This is not a resource value.</exception>
    public ComponentResourceType GetResourceType() => ExpectResource().Type;

    /// <summary>Gets whether this is a resource whose type is a binding host type.</summary>
    /// <returns>True for a resource carrying a host object.</returns>
    public bool IsHostResource() => ResourceCell is HostResourceCell;

    /// <summary>Gets whether this is a resource of the host type <paramref name="type"/>.</summary>
    /// <param name="type">The host resource type.</param>
    /// <returns>True for a resource carrying a host object of <paramref name="type"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> is null.</exception>
    public bool IsHostResource(ComponentHostResourceType type)
    {
        if (type is null)
        {
            throw new ArgumentNullException(nameof(type));
        }

        return ResourceCell is HostResourceCell cell && ReferenceEquals(cell.HostType, type);
    }

    /// <summary>Gets the host object of a resource value.</summary>
    /// <typeparam name="T">The type of the host object.</typeparam>
    /// <param name="type">The expected resource type.</param>
    /// <returns>The host object.</returns>
    /// <remarks>
    /// On a host callback argument this is a view: it throws <see cref="ObjectDisposedException"/> once
    /// the callback has ended, and for an <c>own</c> argument it does not claim ownership (use
    /// <see cref="TakeHostResource{T}"/>). On an export result the host owns the object.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Not a resource, or not of <paramref name="type"/>.</exception>
    /// <exception cref="ObjectDisposedException">An expired callback-argument view.</exception>
    public T AsHostResource<T>(ComponentHostResourceType<T> type) where T : class =>
        (T)ExpectHostResource(type).Value;

    /// <summary>Gets the host object of a resource value of <paramref name="type"/>, if it is one.</summary>
    /// <typeparam name="T">The type of the host object.</typeparam>
    /// <param name="type">The expected resource type.</param>
    /// <param name="value">The host object, or null.</param>
    /// <returns>True when this is a live resource of <paramref name="type"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> is null.</exception>
    public bool TryAsHostResource<T>(ComponentHostResourceType<T> type, [NotNullWhen(true)] out T? value) where T : class
    {
        if (type is null)
        {
            throw new ArgumentNullException(nameof(type));
        }

        if (ResourceCell is HostResourceCell cell && ReferenceEquals(cell.HostType, type) && !cell.Expired &&
            cell.RawValue is T target)
        {
            value = target;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Claims an owned host object received from a component: an <c>own</c> host callback argument, or an
    /// export result. The host owns it and no destructor runs for it.
    /// </summary>
    /// <typeparam name="T">The type of the host object.</typeparam>
    /// <param name="type">The expected resource type.</param>
    /// <returns>The host object.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// Not an owned resource of <paramref name="type"/> received from a component, or already taken.
    /// </exception>
    /// <exception cref="ObjectDisposedException">An expired callback-argument view.</exception>
    public T TakeHostResource<T>(ComponentHostResourceType<T> type) where T : class =>
        (T)ExpectHostResource(type).Claim();

    /// <summary>Gets the native handle of a resource value whose type is not a binding host type.</summary>
    /// <returns>The handle.</returns>
    /// <remarks>
    /// On a host callback argument this is a view that expires when the callback ends: for an <c>own</c>
    /// argument it does not claim ownership (use <see cref="TakeResource"/>), and the binding drops an unclaimed
    /// handle when the callback ends. On an export result the caller owns the handle.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Not a resource, or a binding host resource.</exception>
    /// <exception cref="ObjectDisposedException">An expired callback-argument view.</exception>
    public ComponentResource AsResource() => ExpectNativeResource().Handle;

    /// <summary>
    /// Claims the owned native handle of an <c>own</c> host callback argument, or returns the handle of an
    /// export result. The caller must <see cref="ComponentResource.Drop"/> or
    /// <see cref="ComponentResource.Dispose"/> it, or pass it on.
    /// </summary>
    /// <returns>The handle.</returns>
    /// <exception cref="InvalidOperationException">
    /// Not a resource, a binding host resource, not an owned resource received from a component, or already taken.
    /// </exception>
    /// <exception cref="ObjectDisposedException">An expired callback-argument view.</exception>
    public ComponentResource TakeResource() => ExpectNativeResource().Claim();

    /// <summary>
    /// Disposes every native handle in this value tree that the caller owns, for example the elements of a
    /// <c>list&lt;own&lt;widget&gt;&gt;</c> result. Unclaimed callback-argument views and borrow views are skipped.
    /// </summary>
    /// <remarks>Each handle is disposed with <see cref="ComponentResource.Dispose"/>, which never throws for state reasons.</remarks>
    public void DisposeResources()
    {
        switch (Kind)
        {
            case ComponentValueKind.Resource:
                if (ResourceCell is NativeResourceCell cell && cell.OwnedByCaller)
                {
                    cell.RawHandle.Dispose();
                }

                break;

            case ComponentValueKind.List:
            case ComponentValueKind.Tuple:
                foreach (var item in Items)
                {
                    item?.DisposeResources();
                }

                break;

            case ComponentValueKind.Record:
                foreach (var field in Fields)
                {
                    field.Value?.DisposeResources();
                }

                break;

            case ComponentValueKind.Option:
            case ComponentValueKind.Result:
            case ComponentValueKind.Variant:
                Payload?.DisposeResources();
                break;
        }
    }

    private NativeResourceCell ExpectNativeResource()
    {
        if (ExpectResource() is not NativeResourceCell cell)
        {
            throw new InvalidOperationException(
                $"Expected a resource whose type is not defined by this binding, got {ResourceCell!.DescribeType()}; " +
                "use AsHostResource or TakeHostResource for host resources.");
        }

        return cell;
    }

    private ResourceCell ExpectResource()
    {
        if (Kind != ComponentValueKind.Resource || ResourceCell is not { } cell)
        {
            throw new InvalidOperationException($"Expected component value kind {ComponentValueKind.Resource}, got {Kind}.");
        }

        return cell;
    }

    private HostResourceCell ExpectHostResource(ComponentHostResourceType type)
    {
        if (type is null)
        {
            throw new ArgumentNullException(nameof(type));
        }

        if (ExpectResource() is not HostResourceCell cell || !ReferenceEquals(cell.HostType, type))
        {
            throw new InvalidOperationException(
                $"Expected a resource of type {type.DisplayName}, got {ResourceCell!.DescribeType()}.");
        }

        return cell;
    }

    private string DescribeResource()
    {
        if (ResourceCell is not { } cell)
        {
            return "resource";
        }

        var prefix = cell.Ownership switch
        {
            ComponentResourceOwnership.Own => "own",
            ComponentResourceOwnership.Borrow => "borrow",
            _ => "resource",
        };

        return $"{prefix}<{cell.DescribeType()}>";
    }
}
