using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Wasmtime.Components;

/// <summary>
/// A callback implementing a component function defined by the host.
/// </summary>
/// <param name="arguments">The arguments passed by the guest.</param>
/// <param name="results">
/// The results to return to the guest. Every element must be assigned.
/// </param>
public delegate void ComponentFunctionCallback(
    ReadOnlySpan<ComponentValue> arguments,
    Span<ComponentValue> results);

/// <summary>
/// An instance being defined within a <see cref="ComponentLinker"/>, used to define names into
/// a namespace.
/// </summary>
/// <remarks>
/// Obtaining one of these acquires exclusive access to whatever it came from: neither the owning
/// linker nor a parent instance may be used until this is disposed. This type enforces that by
/// throwing rather than allowing the undefined behaviour the C API warns about.
/// </remarks>
public sealed class ComponentLinkerInstance : IDisposable
{
    private readonly Handle handle;
    private readonly Action onDisposed;
    private readonly ComponentLinker? owner;
    private ComponentLinkerInstance? child;
    private bool disposed;

    internal ComponentLinkerInstance(IntPtr handle, Action onDisposed, ComponentLinker? owner = null)
    {
        this.handle = new Handle(handle);
        this.onDisposed = onDisposed;
        this.owner = owner;
    }

    internal Handle NativeHandle
    {
        get
        {
            if (handle.IsInvalid || handle.IsClosed)
            {
                throw new ObjectDisposedException(typeof(ComponentLinkerInstance).FullName);
            }

            if (child is not null)
            {
                throw new InvalidOperationException(
                    "This linker instance cannot be used while a nested instance created from it is still in use.");
            }

            return handle;
        }
    }

    /// <summary>
    /// Defines a nested instance within this instance.
    /// </summary>
    /// <param name="name">The name of the nested instance.</param>
    /// <returns>The nested instance, which must be disposed before this one is used again.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="name"/> is null.</exception>
    public ComponentLinkerInstance AddInstance(string name)
    {
        if (name is null)
        {
            throw new ArgumentNullException(nameof(name));
        }

        var current = NativeHandle;
        var nameBytes = Encoding.UTF8.GetBytes(name);

        unsafe
        {
            fixed (byte* namePtr = nameBytes)
            {
                var error = Native.wasmtime_component_linker_instance_add_instance(
                    current, namePtr, (nuint)nameBytes.Length, out var nested);

                if (error != IntPtr.Zero)
                {
                    throw WasmtimeException.FromOwnedError(error);
                }

                child = new ComponentLinkerInstance(nested, () => child = null, owner);
                return child;
            }
        }
    }

    /// <summary>
    /// Defines a core WebAssembly module within this instance.
    /// </summary>
    /// <param name="name">The name to define the module as.</param>
    /// <param name="module">The module.</param>
    /// <exception cref="ArgumentNullException">Thrown if an argument is null.</exception>
    public void AddModule(string name, Module module)
    {
        if (name is null)
        {
            throw new ArgumentNullException(nameof(name));
        }

        if (module is null)
        {
            throw new ArgumentNullException(nameof(module));
        }

        var current = NativeHandle;
        var nameBytes = Encoding.UTF8.GetBytes(name);

        unsafe
        {
            fixed (byte* namePtr = nameBytes)
            {
                var error = Native.wasmtime_component_linker_instance_add_module(
                    current, namePtr, (nuint)nameBytes.Length, module.NativeHandle);

                if (error != IntPtr.Zero)
                {
                    throw WasmtimeException.FromOwnedError(error);
                }
            }
        }
    }

    /// <summary>
    /// Defines a function within this instance.
    /// </summary>
    /// <param name="name">The name of the function.</param>
    /// <param name="callback">The implementation of the function.</param>
    /// <exception cref="ArgumentNullException">Thrown if an argument is null.</exception>
    public void DefineFunction(string name, ComponentFunctionCallback callback)
    {
        if (name is null)
        {
            throw new ArgumentNullException(nameof(name));
        }

        if (callback is null)
        {
            throw new ArgumentNullException(nameof(callback));
        }

        var current = NativeHandle;
        var nameBytes = Encoding.UTF8.GetBytes(name);

        // The function's type is the same on every call, so its resource plan is built once.
        var planCache = new StrongBox<ResourcePlan?>();
        Native.ComponentFuncCallback trampoline = (env, context, type, args, nargs, results, nresults) =>
            Invoke(callback, name, context, type, planCache, args, (int)nargs, results, (int)nresults);

        unsafe
        {
            fixed (byte* namePtr = nameBytes)
            {
                var error = Native.wasmtime_component_linker_instance_add_func(
                    current,
                    namePtr,
                    (nuint)nameBytes.Length,
                    trampoline,
                    GCHandle.ToIntPtr(GCHandle.Alloc(trampoline)),
                    Function.Finalizer);

                if (error != IntPtr.Zero)
                {
                    throw WasmtimeException.FromOwnedError(error);
                }
            }
        }
    }

    /// <summary>
    /// Defines <paramref name="type"/> as the resource named <paramref name="name"/> in this instance.
    /// </summary>
    /// <param name="name">
    /// The exact export name in the component's import instance type, for example <c>counter</c>.
    /// </param>
    /// <param name="type">The host resource type.</param>
    /// <remarks>
    /// <para>
    /// The same type may be defined under other names, instances and linkers; they alias. <c>(eq …)</c>
    /// aliases in the component need no definition.
    /// </para>
    /// <para>
    /// When a guest drops an owned handle, the type's destructor runs on the thread running the
    /// WebAssembly code.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="WasmtimeException">Wasmtime rejected the definition, for example a duplicate name.</exception>
    public void DefineResource(string name, ComponentHostResourceType type)
    {
        if (name is null)
        {
            throw new ArgumentNullException(nameof(name));
        }

        if (type is null)
        {
            throw new ArgumentNullException(nameof(type));
        }

        var current = NativeHandle;
        var nameBytes = Encoding.UTF8.GetBytes(name);

        ComponentResourceNative.ResourceDestructor trampoline = (env, context, rep) =>
            InvokeDestructor(type, context, rep);

        unsafe
        {
            fixed (byte* namePtr = nameBytes)
            {
                var data = GCHandle.ToIntPtr(GCHandle.Alloc(trampoline));
                IntPtr error;
                try
                {
                    // From here Wasmtime owns data and runs the finalizer, even on error.
                    error = ComponentResourceNative.wasmtime_component_linker_instance_add_resource(
                        current,
                        namePtr,
                        (nuint)nameBytes.Length,
                        type.NativeHandle,
                        trampoline,
                        data,
                        Function.Finalizer);
                }
                catch (ObjectDisposedException)
                {
                    // Marshalling failed before Wasmtime took ownership of data.
                    GCHandle.FromIntPtr(data).Free();
                    throw;
                }

                if (error != IntPtr.Zero)
                {
                    throw WasmtimeException.FromOwnedError(error);
                }
            }
        }

        type.RecordName(name);
        owner?.AddHostResourceType(type);
    }

    /// <summary>
    /// Creates a new host resource type with <paramref name="destructor"/>, defines it as
    /// <paramref name="name"/> in this instance, and returns it.
    /// </summary>
    /// <typeparam name="T">The type of the host objects.</typeparam>
    /// <param name="name">The exact export name in the component's import instance type.</param>
    /// <param name="destructor">
    /// Runs when a guest-owned object is destroyed, or null to use <see cref="IDisposable.Dispose"/> when
    /// <typeparamref name="T"/> implements it.
    /// </param>
    /// <returns>The new resource type.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    /// <exception cref="WasmtimeException">Wasmtime rejected the definition.</exception>
    public ComponentHostResourceType<T> DefineResource<T>(string name, ComponentResourceDestructor<T>? destructor = null)
        where T : class
    {
        var type = new ComponentHostResourceType<T>(destructor);
        DefineResource(name, type);
        return type;
    }

    /// <summary>
    /// Runs the destructor for the host object behind <paramref name="rep"/>, which a guest dropped.
    /// </summary>
    private static IntPtr InvokeDestructor(ComponentHostResourceType type, IntPtr context, uint rep)
    {
        try
        {
            var store = new StoreContext(context).Store;
            var value = store.ComponentResourceTable.ReleaseFromGuest(type, rep);
            type.Destroy(value);
            return IntPtr.Zero;
        }
        catch (Exception ex)
        {
            return HandleCallbackException(ex);
        }
    }

    /// <summary>
    /// Runs a host callback: reads the arguments, calls the user delegate and writes the results.
    /// </summary>
    /// <param name="callback">The user delegate.</param>
    /// <param name="name">The function name, for diagnostics.</param>
    /// <param name="context">The Store context Wasmtime passed to the callback.</param>
    /// <param name="type">The function type Wasmtime passed to the callback, used to check returned resources.</param>
    /// <param name="planCache">The function's resource plan, cached across calls.</param>
    /// <param name="args">The arguments.</param>
    /// <param name="argumentCount">The number of arguments.</param>
    /// <param name="results">The results to write.</param>
    /// <param name="resultCount">The number of results.</param>
    /// <returns>Null on success, otherwise an owned <c>wasmtime_error_t*</c>.</returns>
    private static IntPtr Invoke(
        ComponentFunctionCallback callback,
        string name,
        IntPtr context,
        IntPtr type,
        StrongBox<ResourcePlan?> planCache,
        IntPtr args,
        int argumentCount,
        IntPtr results,
        int resultCount)
    {
        ComponentValue[]? arguments = null;
        ComponentValue[]? produced = null;
        var resources = new CallbackResources(context);

        try
        {
            arguments = argumentCount == 0
                ? Array.Empty<ComponentValue>()
                : ArrayPool<ComponentValue>.Shared.Rent(argumentCount);
            for (var i = 0; i < argumentCount; i++)
            {
                arguments[i] = ComponentValueMarshaller.Read(args + (i * ComponentValueMarshaller.ValueSize), resources);
            }

            // Converts every resource argument before user code runs; borrows are released here.
            resources.ResolveArguments();

            produced = resultCount == 0
                ? Array.Empty<ComponentValue>()
                : ArrayPool<ComponentValue>.Shared.Rent(resultCount);
            Array.Clear(produced, 0, resultCount);
            callback(arguments.AsSpan(0, argumentCount), produced.AsSpan(0, resultCount));

            // A resource of the wrong type must fail here, while it is still the host's.
            resources.CheckResultResources(type, produced.AsSpan(0, resultCount), planCache);

            for (var i = 0; i < resultCount; i++)
            {
                if (produced[i] is null)
                {
                    throw new InvalidOperationException(
                        $"The callback for component function '{name}' did not assign result {i}.");
                }

                ComponentValueMarshaller.WriteOwned(
                    produced[i],
                    results + (i * ComponentValueMarshaller.ValueSize),
                    resources);
            }

            // Release unclaimed owned arguments before completing the results: the results' reservations
            // already exist, so an argument handed back is kept. If a destructor fails, the callback fails and
            // nothing it returned is delivered, so the returned objects are destroyed now.
            if (resources.EndCallback() is { } failure)
            {
                resources.AbortResults();
                return HandleCallbackException(failure);
            }

            resources.CompleteResults();
            return IntPtr.Zero;
        }
        catch (Exception ex)
        {
            try
            {
                resources.AbortResults();
                resources.EndCallback();
            }
            catch (Exception)
            {
                // The callback's own failure is the one reported.
            }

            return HandleCallbackException(ex);
        }
        finally
        {
            if (arguments is { Length: > 0 })
            {
                ArrayPool<ComponentValue>.Shared.Return(arguments, clearArray: true);
            }

            if (produced is { Length: > 0 })
            {
                ArrayPool<ComponentValue>.Shared.Return(produced, clearArray: true);
            }
        }
    }

    /// <summary>
    /// Converts a managed exception into an owned <c>wasmtime_error_t</c>, since an exception
    /// must never propagate across the native-to-managed transition.
    /// </summary>
    private static IntPtr HandleCallbackException(Exception ex)
    {
        try
        {
            Function.CallbackErrorCause = ex is WasmtimeException wasmtimeException
                ? wasmtimeException.InnerException
                : ex;

            return Native.wasmtime_error_new(ex.Message);
        }
        catch (Exception separateException)
        {
            // See Function.HandleCallbackException: unwinding through native frames is undefined
            // behaviour, so failing fast is the only safe option left.
            Environment.FailFast(separateException.Message, separateException);
            throw;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        // A nested instance borrows from this one, so it has to go first.
        child?.Dispose();
        handle.Dispose();
        onDisposed();
    }

    internal class Handle
        : SafeHandleZeroOrMinusOneIsInvalid
    {
        public Handle(IntPtr handle)
            : base(true)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle()
        {
            Native.wasmtime_component_linker_instance_delete(handle);
            return true;
        }
    }

    internal static class Native
    {
        public delegate IntPtr ComponentFuncCallback(
            IntPtr env,
            IntPtr context,
            IntPtr type,
            IntPtr args,
            nuint nargs,
            IntPtr results,
            nuint nresults);

        [DllImport(Engine.LibraryName)]
        public static extern unsafe IntPtr wasmtime_component_linker_instance_add_instance(
            Handle linker_instance, byte* name, nuint name_len, out IntPtr linker_instance_out);

        [DllImport(Engine.LibraryName)]
        public static extern unsafe IntPtr wasmtime_component_linker_instance_add_module(
            Handle linker_instance, byte* name, nuint name_len, Module.Handle module);

        [DllImport(Engine.LibraryName)]
        public static extern unsafe IntPtr wasmtime_component_linker_instance_add_func(
            Handle linker_instance,
            byte* name,
            nuint name_len,
            ComponentFuncCallback callback,
            IntPtr data,
            Function.Native.Finalizer? finalizer);

        [DllImport(Engine.LibraryName)]
        public static extern void wasmtime_component_linker_instance_delete(
            IntPtr /* wasmtime_component_linker_instance_t* */ linker_instance);

        [DllImport(Engine.LibraryName)]
        public static extern IntPtr wasmtime_error_new([MarshalAs(Extensions.LPUTF8Str)] string message);
    }
}
