using System;
using System.Globalization;
using System.Threading;

namespace Wasmtime.Components;

/// <summary>Non-generic view of a resource type whose values are managed objects owned by the host.</summary>
/// <remarks>Create instances with <see cref="ComponentHostResourceType{T}"/>.</remarks>
public abstract class ComponentHostResourceType : ComponentResourceType
{
    // Process-wide ids start well above small hand-picked ids other C API users might choose.
    private const uint FirstId = 0x4000_0000;
    private static int lastId = unchecked((int)FirstId) - 1;

    private readonly object gate = new();
    private SafeResourceTypeHandle? nativeHandle;
    private string? name;

    private protected ComponentHostResourceType()
    {
        Id = unchecked((uint)Interlocked.Increment(ref lastId));
    }

    /// <summary>Gets whether this type was created by this binding, which is always true.</summary>
    public sealed override bool IsHostDefined => true;

    /// <summary>Gets the CLR type of the objects carried by this resource type.</summary>
    public abstract Type ValueType { get; }

    /// <summary>
    /// Gets the name this type was first defined under (for diagnostics only), or null before any
    /// <see cref="ComponentLinkerInstance.DefineResource(string, ComponentHostResourceType)"/>.
    /// </summary>
    public string? Name => Volatile.Read(ref name);

    /// <summary>The process-wide type id passed to Wasmtime as the host resource type.</summary>
    internal uint Id { get; }

    internal override SafeResourceTypeHandle NativeHandle
    {
        get
        {
            lock (gate)
            {
                return nativeHandle ??= new SafeResourceTypeHandle(
                    ComponentResourceNative.wasmtime_component_resource_type_new_host(Id));
            }
        }
    }

    /// <summary>Runs the destructor for <paramref name="value"/>; exceptions propagate.</summary>
    internal abstract void Destroy(object value);

    internal void RecordName(string definedName) =>
        Interlocked.CompareExchange(ref name, definedName, null);

    /// <summary>The name used in diagnostics: the defined name, or the CLR type name.</summary>
    internal string DisplayName =>
        string.Format(CultureInfo.InvariantCulture, "{0}#{1}", Name ?? ValueType.Name, Id - FirstId + 1);

    /// <inheritdoc/>
    public override int GetHashCode() => unchecked((int)Id);

    /// <summary>Describes this type, for example <c>counter (Counter, host #3)</c>.</summary>
    /// <returns>The description.</returns>
    public override string ToString() =>
        string.Format(CultureInfo.InvariantCulture, "{0} ({1}, host #{2})",
            Name ?? ValueType.Name, ValueType.Name, Id - FirstId + 1);
}

/// <summary>Runs when a host object of a resource type is destroyed.</summary>
/// <typeparam name="T">The type of the host objects.</typeparam>
/// <param name="value">The object being destroyed.</param>
public delegate void ComponentResourceDestructor<in T>(T value) where T : class;

/// <summary>A resource type whose values are managed objects of type <typeparamref name="T"/> owned by the host.</summary>
/// <typeparam name="T">The type of the host objects.</typeparam>
/// <remarks>
/// <para>
/// Each instance gets a fresh process-wide id, so two instances never alias, even for the same
/// <typeparamref name="T"/>. It needs no linker: values can be built before any linker exists. Define it
/// into a linker instance with <see cref="ComponentLinkerInstance.DefineResource(string, ComponentHostResourceType)"/>;
/// the same type may be defined under several names, instances and linkers, which then alias.
/// </para>
/// <para>
/// The destructor runs exactly once per destruction event: when a guest drops its owning handle; when a
/// callback ends without claiming an owned argument of this type; when the binding cannot deliver an
/// object a callback returned; or for objects guests still own when
/// <see cref="Store.ReleaseComponentResources"/> runs. It never runs for objects the host owns. When the
/// destructor is null and <typeparamref name="T"/> implements <see cref="IDisposable"/>, Dispose is used;
/// otherwise nothing runs.
/// </para>
/// <para>
/// A guest-triggered destructor runs on the thread running the WebAssembly code. An exception traps the
/// guest operation that caused the destruction and becomes the InnerException of that call's
/// <see cref="WasmtimeException"/>.
/// </para>
/// </remarks>
public sealed class ComponentHostResourceType<T> : ComponentHostResourceType where T : class
{
    private readonly ComponentResourceDestructor<T>? destructor;

    /// <summary>Creates a new host resource type.</summary>
    /// <param name="destructor">
    /// Runs when a guest-owned object is destroyed, or null to use <see cref="IDisposable.Dispose"/> when
    /// <typeparamref name="T"/> implements it.
    /// </param>
    public ComponentHostResourceType(ComponentResourceDestructor<T>? destructor = null)
    {
        this.destructor = destructor;
    }

    /// <inheritdoc/>
    public override Type ValueType => typeof(T);

    /// <summary>Gets whether a guest of <paramref name="store"/> currently owns <paramref name="value"/>.</summary>
    /// <param name="store">The store.</param>
    /// <param name="value">The host object.</param>
    /// <returns>True when a guest of the store owns the object through this type.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public bool IsHeldByGuest(Store store, T value)
    {
        if (store is null)
        {
            throw new ArgumentNullException(nameof(store));
        }

        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        return store.PeekComponentResourceTable()?.IsHeldByGuest(this, value) ?? false;
    }

    /// <summary>Gets the number of objects of this type the guests of <paramref name="store"/> own.</summary>
    /// <param name="store">The store.</param>
    /// <returns>The number of guest-owned objects.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="store"/> is null.</exception>
    public int CountHeldByGuest(Store store)
    {
        if (store is null)
        {
            throw new ArgumentNullException(nameof(store));
        }

        return store.PeekComponentResourceTable()?.CountHeldByGuest(this) ?? 0;
    }

    internal override void Destroy(object value)
    {
        var typed = (T)value;
        if (destructor is not null)
        {
            destructor(typed);
        }
        else if (typed is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
