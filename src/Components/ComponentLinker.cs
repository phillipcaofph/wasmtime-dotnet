using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Wasmtime.Components;

/// <summary>
/// Used to instantiate a <see cref="Component"/>, providing the host functions and other
/// definitions that satisfy its imports.
/// </summary>
public class ComponentLinker
    : IDisposable
{
    private readonly Handle handle;
    private ComponentLinkerInstance? root;
    private bool isolateHostCallbacks;

    /// <summary>
    /// Creates a new <see cref="ComponentLinker"/> for the given engine.
    /// </summary>
    /// <param name="engine">The engine to create the linker for.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="engine"/> is null.</exception>
    public ComponentLinker(Engine engine)
    {
        if (engine is null)
        {
            throw new ArgumentNullException(nameof(engine));
        }

        handle = new Handle(Native.wasmtime_component_linker_new(engine.NativeHandle));

        // Managed code on a Wasmtime fiber can crash the CLR, so async engines isolate by default.
        isolateHostCallbacks = engine.IsComponentModelAsyncEnabled && HostCallbackDispatcher.IsSupported;
    }

    internal ComponentLinker(IntPtr handle)
    {
        this.handle = new Handle(handle);
    }

    internal Handle NativeHandle
    {
        get
        {
            if (handle.IsInvalid || handle.IsClosed)
            {
                throw new ObjectDisposedException(typeof(ComponentLinker).FullName);
            }

            if (root is not null)
            {
                throw new InvalidOperationException(
                    "The linker cannot be used while the linker instance returned by Root() is still in use.");
            }

            return handle;
        }
    }

    /// <summary>
    /// Sets whether later definitions are allowed to shadow previous ones.
    /// </summary>
    /// <value>True to allow shadowing.</value>
    public bool AllowShadowing
    {
        set
        {
            Native.wasmtime_component_linker_allow_shadowing(NativeHandle, value);
        }
    }

    /// <summary>
    /// Gets or sets whether host functions defined through
    /// <see cref="ComponentLinkerInstance.DefineFunction"/> run on isolated native worker
    /// threads instead of on Wasmtime fiber stacks. See <see cref="HostCallbackIsolation"/>.
    /// The value is captured when <see cref="Root"/> is called, and nested instances inherit it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Defaults to true when the engine enables asynchronous component support
    /// (<see cref="Config.WithComponentModelAsync(bool)"/>) and
    /// <see cref="HostCallbackIsolation.IsSupported"/> is true, and to false otherwise. Disabling
    /// it for asynchronous calls runs managed code on Wasmtime fiber stacks, which the .NET
    /// garbage collector cannot safely scan.
    /// </para>
    /// <para>
    /// Inside an isolated callback, the calling <see cref="Store"/> only supports <see cref="Store.Fuel"/>,
    /// <see cref="Store.GC"/> and <see cref="Store.SetEpochDeadline"/>; other Store use throws.
    /// Isolation adds a few microseconds per call.
    /// </para>
    /// </remarks>
    /// <exception cref="PlatformNotSupportedException">
    /// Thrown when enabling isolation and <see cref="HostCallbackIsolation.IsSupported"/> is false.
    /// </exception>
    public bool IsolateHostCallbacks
    {
        get => isolateHostCallbacks;
        set
        {
            if (value)
            {
                HostCallbackDispatcher.ThrowIfUnsupported();
            }

            isolateHostCallbacks = value;
        }
    }

    /// <summary>
    /// Returns the root instance of this linker, used to define names into the root namespace.
    /// </summary>
    /// <returns>The root instance. The linker cannot be used again until this is disposed.</returns>
    public ComponentLinkerInstance Root()
    {
        var current = NativeHandle;
        root = new ComponentLinkerInstance(
            Native.wasmtime_component_linker_root(current),
            () => root = null,
            isolateHostCallbacks);

        return root;
    }

    /// <summary>
    /// Adds all WASI Preview 2 interfaces to this linker.
    /// </summary>
    /// <remarks>
    /// The store used for instantiation must have a WASI configuration set with
    /// <see cref="Store.SetWasiConfiguration"/>.
    /// </remarks>
    public void AddWasiPreview2()
    {
        var error = Native.wasmtime_component_linker_add_wasip2(NativeHandle);
        if (error != IntPtr.Zero)
        {
            throw WasmtimeException.FromOwnedError(error);
        }
    }

    /// <summary>
    /// Defines every import of the given component that is not already defined as a function
    /// which traps when called.
    /// </summary>
    /// <param name="component">The component whose imports should be satisfied.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="component"/> is null.</exception>
    public void DefineUnknownImportsAsTraps(Component component)
    {
        if (component is null)
        {
            throw new ArgumentNullException(nameof(component));
        }

        var error = Native.wasmtime_component_linker_define_unknown_imports_as_traps(
            NativeHandle, component.NativeHandle);

        if (error != IntPtr.Zero)
        {
            throw WasmtimeException.FromOwnedError(error);
        }
    }

    /// <summary>
    /// Instantiates a component in the given store.
    /// </summary>
    /// <param name="store">The store to instantiate the component in.</param>
    /// <param name="component">The component to instantiate.</param>
    /// <returns>The instantiated component.</returns>
    /// <exception cref="ArgumentNullException">Thrown if an argument is null.</exception>
    /// <exception cref="WasmtimeException">Thrown if an import could not be satisfied.</exception>
    public ComponentInstance Instantiate(Store store, Component component)
    {
        if (store is null)
        {
            throw new ArgumentNullException(nameof(store));
        }

        if (component is null)
        {
            throw new ArgumentNullException(nameof(component));
        }

        store.BeginComponentOperation();

        // Failures are captured and rethrown once cleanup has run; see ComponentFunction.ThrowIfFailed.
        ComponentInstance? result = null;
        Exception? failure = null;
        try
        {
            var context = store.Context.handle;
            var error = Native.wasmtime_component_linker_instantiate(
                NativeHandle, context, component.NativeHandle, out var instance);

            GC.KeepAlive(store);

            if (error != IntPtr.Zero)
            {
                failure = HostCallbackDispatcher.AttachCause(WasmtimeException.FromOwnedError(error), context);
            }
            else
            {
                result = new ComponentInstance(store, instance);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        store.EndComponentOperation();
        ComponentFunction.ThrowIfFailed(failure);
        return result!;
    }

    /// <summary>
    /// Instantiates a component using Wasmtime's asynchronous component API.
    /// </summary>
    /// <param name="store">The store to instantiate the component in.</param>
    /// <param name="component">The component to instantiate.</param>
    /// <param name="cancellationToken">A token that cancels polling and disposes the native instantiation future.</param>
    /// <returns>The instantiated component.</returns>
    /// <exception cref="InvalidOperationException">The engine was not configured for async components.</exception>
    /// <exception cref="WasmtimeException">An import could not be satisfied or instantiation failed.</exception>
    /// <remarks>
    /// The engine must be configured with <see cref="Config.WithComponentModelAsync(bool)"/>.
    /// Do not use this store for any other operation until the returned task completes.
    /// Cancellation disposes the native instantiation future; it does not roll back guest side effects.
    /// Startup code runs on a Wasmtime fiber stack. Host functions are isolated from it by default;
    /// see <see cref="IsolateHostCallbacks"/>.
    /// </remarks>
    public async Task<ComponentInstance> InstantiateAsync(
        Store store,
        Component component,
        CancellationToken cancellationToken = default)
    {
        if (store is null)
        {
            throw new ArgumentNullException(nameof(store));
        }

        if (component is null)
        {
            throw new ArgumentNullException(nameof(component));
        }

        if (!store.IsComponentModelAsyncEnabled)
        {
            throw new InvalidOperationException(
                "Asynchronous component instantiation requires an engine configured with WithComponentModelAsync(true).");
        }

        cancellationToken.ThrowIfCancellationRequested();
        store.BeginComponentOperation();

        // Failures are captured and rethrown once cleanup has run; see ComponentFunction.ThrowIfFailed.
        var instanceBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<ComponentInstance.Native.Instance>());
        var errorBuffer = Marshal.AllocHGlobal(IntPtr.Size);
        Marshal.StructureToPtr(default(ComponentInstance.Native.Instance), instanceBuffer, false);
        Marshal.WriteIntPtr(errorBuffer, IntPtr.Zero);
        var future = IntPtr.Zero;
        ComponentInstance? result = null;
        Exception? failure = null;
        try
        {
            var context = store.Context.handle;
            future = Native.wasmtime_component_linker_instantiate_async(
                NativeHandle,
                context,
                component.NativeHandle,
                instanceBuffer,
                errorBuffer);

            if (future == IntPtr.Zero)
            {
                var error = Marshal.ReadIntPtr(errorBuffer);
                Marshal.WriteIntPtr(errorBuffer, IntPtr.Zero);
                failure = error != IntPtr.Zero
                    ? WasmtimeException.FromOwnedError(error)
                    : new InvalidOperationException("Wasmtime failed to create an asynchronous instantiation future.");
            }
            else
            {
                await ComponentFunction.PollFutureAsync(future, store, context, cancellationToken).ConfigureAwait(false);
                ComponentFunction.Native.wasmtime_call_future_delete(future);
                future = IntPtr.Zero;
                var error = Marshal.ReadIntPtr(errorBuffer);
                if (error != IntPtr.Zero)
                {
                    Marshal.WriteIntPtr(errorBuffer, IntPtr.Zero);
                    failure = HostCallbackDispatcher.AttachCause(WasmtimeException.FromOwnedError(error), context);
                }
                else
                {
                    failure = HostCallbackDispatcher.TakeLateHostFailure(context);
                    if (failure is null)
                    {
                        var instance = Marshal.PtrToStructure<ComponentInstance.Native.Instance>(instanceBuffer);
                        result = new ComponentInstance(store, instance);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        if (future != IntPtr.Zero)
        {
            ComponentFunction.Native.wasmtime_call_future_delete(future);
        }

        var pendingError = Marshal.ReadIntPtr(errorBuffer);
        if (pendingError != IntPtr.Zero)
        {
            ComponentFunction.Native.wasmtime_error_delete(pendingError);
        }

        Marshal.FreeHGlobal(errorBuffer);
        Marshal.FreeHGlobal(instanceBuffer);
        GC.KeepAlive(this);
        GC.KeepAlive(component);
        GC.KeepAlive(store);
        store.EndComponentOperation();
        ComponentFunction.ThrowIfFailed(failure);
        return result!;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        root?.Dispose();
        handle.Dispose();
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
            Native.wasmtime_component_linker_delete(handle);
            return true;
        }
    }

    internal static class Native
    {
        [DllImport(Engine.LibraryName)]
        public static extern IntPtr wasmtime_component_linker_new(Engine.Handle engine);

        [DllImport(Engine.LibraryName)]
        public static extern void wasmtime_component_linker_allow_shadowing(
            Handle linker, [MarshalAs(UnmanagedType.I1)] bool allow);

        [DllImport(Engine.LibraryName)]
        public static extern IntPtr wasmtime_component_linker_root(Handle linker);

        [DllImport(Engine.LibraryName)]
        public static extern IntPtr wasmtime_component_linker_instantiate(
            Handle linker,
            IntPtr context,
            Component.Handle component,
            out ComponentInstance.Native.Instance instance_out);

        [DllImport(Engine.LibraryName)]
        public static extern IntPtr wasmtime_component_linker_instantiate_async(
            Handle linker,
            IntPtr context,
            Component.Handle component,
            IntPtr instance_out,
            IntPtr error_ret);

        [DllImport(Engine.LibraryName)]
        public static extern IntPtr wasmtime_component_linker_define_unknown_imports_as_traps(
            Handle linker, Component.Handle component);

        [DllImport(Engine.LibraryName)]
        public static extern IntPtr wasmtime_component_linker_add_wasip2(Handle linker);

        [DllImport(Engine.LibraryName)]
        public static extern void wasmtime_component_linker_delete(IntPtr /* wasmtime_component_linker_t* */ linker);
    }
}