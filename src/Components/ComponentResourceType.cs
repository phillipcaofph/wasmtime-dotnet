using System;

namespace Wasmtime.Components;

/// <summary>Identifies a component resource type.</summary>
/// <remarks>
/// Binding host types come from <see cref="ComponentHostResourceType{T}"/>. Native types (guest-defined
/// types, WASI and other host types defined outside this binding, and
/// <see cref="ComponentLinker.DefineUnknownImportsAsTraps(Component)"/> stubs) are read from values
/// through <see cref="ComponentResource.Type"/> or <see cref="ComponentValue.GetResourceType"/>. A
/// guest-defined type is unique per instantiation.
/// </remarks>
public abstract class ComponentResourceType : IEquatable<ComponentResourceType>
{
    private protected ComponentResourceType()
    {
    }

    /// <summary>
    /// Gets whether this type was created by this binding (a <see cref="ComponentHostResourceType"/>).
    /// </summary>
    public abstract bool IsHostDefined { get; }

    /// <summary>The owned native type, used for comparisons and linker definitions.</summary>
    internal abstract SafeResourceTypeHandle NativeHandle { get; }

    /// <summary>
    /// Compares two resource types. Binding host types compare by identity; native types with
    /// <c>wasmtime_component_resource_type_equal</c>. A binding host type never equals a native type.
    /// </summary>
    /// <param name="other">The type to compare with.</param>
    /// <returns>True if both describe the same resource type.</returns>
    public bool Equals(ComponentResourceType? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (IsHostDefined || other.IsHostDefined)
        {
            return false;
        }

        return NativeHandle.NativeEquals(other.NativeHandle);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as ComponentResourceType);

    /// <summary>
    /// Host types hash by their identity; native types share one bucket, because the C API offers no hash.
    /// </summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode() => 0;

    /// <inheritdoc/>
    public override string ToString() => "native resource";
}

/// <summary>A resource type that was not created by this binding, read from a native value.</summary>
internal sealed class NativeResourceType : ComponentResourceType
{
    private readonly SafeResourceTypeHandle handle;

    public NativeResourceType(SafeResourceTypeHandle handle) => this.handle = handle;

    public override bool IsHostDefined => false;

    internal override SafeResourceTypeHandle NativeHandle => handle;
}
