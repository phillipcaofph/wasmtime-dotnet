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
    /// Defaults to false. Enable it when calling components asynchronously: asynchronous calls
    /// otherwise run managed host callbacks on Wasmtime fiber stacks, which the .NET garbage
    /// collector cannot safely scan.
    /// </para>
    /// <para>
    /// Inside an isolated callback the calling <see cref="Store"/> cannot be used, and throws.
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
        try
        {
            var context = store.Context.handle;
            var error = Native.wasmtime_component_linker_instantiate(
                NativeHandle, context, component.NativeHandle, out var instance);

            GC.KeepAlive(store);

            if (error != IntPtr.Zero)
            {
                throw HostCallbackDispatcher.AttachCause(WasmtimeException.FromOwnedError(error), context);
            }

            return new ComponentInstance(store, instance);
        }
        finally
        {
            store.EndComponentOperation();
        }
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
    /// Host functions reached by startup code run on a Wasmtime fiber stack, which the .NET
    /// garbage collector cannot safely scan, so managed host callbacks can crash the process.
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
        var instanceBuffer = IntPtr.Zero;
        var errorBuffer = IntPtr.Zero;
        var future = IntPtr.Zero;
        try
        {
            instanceBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<ComponentInstance.Native.Instance>());
            errorBuffer = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.StructureToPtr(default(ComponentInstance.Native.Instance), instanceBuffer, false);
            Marshal.WriteIntPtr(errorBuffer, IntPtr.Zero);

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
                if (error != IntPtr.Zero)
                {
                    Marshal.WriteIntPtr(errorBuffer, IntPtr.Zero);
                    throw WasmtimeException.FromOwnedError(error);
                }

                throw new InvalidOperationException("Wasmtime failed to create an asynchronous instantiation future.");
            }

            try
            {
                await ComponentFunction.PollFutureAsync(future, store, cancellationToken).ConfigureAwait(false);
                ComponentFunction.Native.wasmtime_call_future_delete(future);
                future = IntPtr.Zero;
                var error = Marshal.ReadIntPtr(errorBuffer);
                if (error != IntPtr.Zero)
                {
                    Marshal.WriteIntPtr(errorBuffer, IntPtr.Zero);
                    throw HostCallbackDispatcher.AttachCause(WasmtimeException.FromOwnedError(error), context);
                }

                var instance = Marshal.PtrToStructure<ComponentInstance.Native.Instance>(instanceBuffer);
                return new ComponentInstance(store, instance);
            }
            finally
            {
                if (future != IntPtr.Zero)
                {
                    ComponentFunction.Native.wasmtime_call_future_delete(future);
                    future = IntPtr.Zero;
                }
                var error = Marshal.ReadIntPtr(errorBuffer);
                if (error != IntPtr.Zero)
                {
                    Marshal.WriteIntPtr(errorBuffer, IntPtr.Zero);
                    ComponentFunction.Native.wasmtime_error_delete(error);
                }
            }
        }
        finally
        {
            if (future != IntPtr.Zero)
            {
                ComponentFunction.Native.wasmtime_call_future_delete(future);
            }
            if (errorBuffer != IntPtr.Zero)
            {
                var error = Marshal.ReadIntPtr(errorBuffer);
                if (error != IntPtr.Zero)
                {
                    ComponentFunction.Native.wasmtime_error_delete(error);
                }
                Marshal.FreeHGlobal(errorBuffer);
            }
            if (instanceBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(instanceBuffer);
            }

            GC.KeepAlive(this);
            GC.KeepAlive(component);
            GC.KeepAlive(store);
            store.EndComponentOperation();
        }
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