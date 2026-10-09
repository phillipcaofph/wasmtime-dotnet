using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
/// An asynchronous host implementation of a component function.
/// </summary>
/// <param name="arguments">A copy of the arguments passed by the guest.</param>
/// <param name="cancellationToken">Cancelled when Wasmtime drops the pending call.</param>
/// <returns>The results, one per component function result (an empty array for none).</returns>
/// <remarks>
/// The <see cref="Store"/> that called the function cannot be used from the callback or any
/// of its continuations, except for <see cref="Store.Fuel"/>, <see cref="Store.GC"/> and
/// <see cref="Store.SetEpochDeadline"/> while the call is running.
/// </remarks>
public delegate Task<ComponentValue[]> ComponentAsyncFunctionCallback(
    ComponentValue[] arguments,
    CancellationToken cancellationToken);

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
    private readonly bool isolateHostCallbacks;
    private ComponentLinkerInstance? child;
    private bool disposed;

    internal ComponentLinkerInstance(IntPtr handle, Action onDisposed, bool isolateHostCallbacks = false)
    {
        this.handle = new Handle(handle);
        this.onDisposed = onDisposed;
        this.isolateHostCallbacks = isolateHostCallbacks;
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

                child = new ComponentLinkerInstance(nested, () => child = null, isolateHostCallbacks);
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
    /// <remarks>
    /// When the owning linker had <see cref="ComponentLinker.IsolateHostCallbacks"/> enabled
    /// before this instance was obtained, the callback runs on an isolated worker thread.
    /// </remarks>
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

        if (isolateHostCallbacks)
        {
            HostCallbackDispatcher.DefineFunction(NativeHandle, name, callback);
            return;
        }

        DefineDirectFunction(name, callback);
    }

    /// <summary>
    /// Defines a function implemented asynchronously by the host. The callback always runs
    /// isolated from Wasmtime fiber stacks, see <see cref="HostCallbackIsolation"/>.
    /// </summary>
    /// <param name="name">The name of the function.</param>
    /// <param name="callback">The implementation of the function.</param>
    /// <remarks>
    /// <para>
    /// The guest is suspended while the returned task is pending, so the component must be
    /// instantiated with <see cref="ComponentLinker.InstantiateAsync"/> on a Store whose engine
    /// enables asynchronous support, and the function must be called with
    /// <see cref="ComponentFunction.CallAsync(ComponentValue[])"/>.
    /// </para>
    /// <para>
    /// If the task fails after the guest suspended, Wasmtime cannot trap the call. The guest
    /// sees placeholder results and the failure is thrown from the pending
    /// <see cref="ComponentFunction.CallAsync(ComponentValue[])"/> instead; a host import returning <c>bool</c>
    /// receives a deliberately mistyped value so the call traps.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown if an argument is null.</exception>
    /// <exception cref="PlatformNotSupportedException">
    /// Thrown when <see cref="HostCallbackIsolation.IsSupported"/> is false.
    /// </exception>
    public void DefineAsyncFunction(string name, ComponentAsyncFunctionCallback callback)
    {
        if (name is null)
        {
            throw new ArgumentNullException(nameof(name));
        }

        if (callback is null)
        {
            throw new ArgumentNullException(nameof(callback));
        }

        HostCallbackDispatcher.DefineAsyncFunction(NativeHandle, name, callback);
    }

    private void DefineDirectFunction(string name, ComponentFunctionCallback callback)
    {
        var current = NativeHandle;
        var nameBytes = Encoding.UTF8.GetBytes(name);

        Native.ComponentFuncCallback trampoline = (env, context, type, args, nargs, results, nresults) =>
            Invoke(callback, name, args, (int)nargs, results, (int)nresults);

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

    internal static IntPtr Invoke(
        ComponentFunctionCallback callback,
        string name,
        IntPtr args,
        int argumentCount,
        IntPtr results,
        int resultCount)
    {
        ComponentValue[]? arguments = null;
        ComponentValue[]? produced = null;

        // The exception is converted after cleanup instead of inside the catch block; see
        // ComponentFunction.ThrowIfFailed.
        Exception? failure = null;
        try
        {
            arguments = argumentCount == 0
                ? Array.Empty<ComponentValue>()
                : ArrayPool<ComponentValue>.Shared.Rent(argumentCount);
            for (var i = 0; i < argumentCount; i++)
            {
                arguments[i] = ComponentValueMarshaller.Read(args + (i * ComponentValueMarshaller.ValueSize));
            }

            produced = resultCount == 0
                ? Array.Empty<ComponentValue>()
                : ArrayPool<ComponentValue>.Shared.Rent(resultCount);
            Array.Clear(produced, 0, resultCount);
            callback(arguments.AsSpan(0, argumentCount), produced.AsSpan(0, resultCount));

            for (var i = 0; i < resultCount; i++)
            {
                if (produced[i] is null)
                {
                    throw new InvalidOperationException(
                        $"The callback for component function '{name}' did not assign result {i}.");
                }

                ComponentValueMarshaller.WriteOwned(
                    produced[i],
                    results + (i * ComponentValueMarshaller.ValueSize));
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        if (arguments is { Length: > 0 })
        {
            ArrayPool<ComponentValue>.Shared.Return(arguments, clearArray: true);
        }

        if (produced is { Length: > 0 })
        {
            ArrayPool<ComponentValue>.Shared.Return(produced, clearArray: true);
        }

        return failure is null ? IntPtr.Zero : HandleCallbackException(failure);
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
