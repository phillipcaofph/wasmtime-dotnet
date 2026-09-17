using System;
using System.Runtime.InteropServices;
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
    /// <param name="allow">True to allow shadowing.</param>
    public void AllowShadowing(bool allow)
    {
        Native.wasmtime_component_linker_allow_shadowing(NativeHandle, allow);
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
            () => root = null);

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

        var error = Native.wasmtime_component_linker_instantiate(
            NativeHandle, store.Context.handle, component.NativeHandle, out var instance);

        GC.KeepAlive(store);

        if (error != IntPtr.Zero)
        {
            throw WasmtimeException.FromOwnedError(error);
        }

        return new ComponentInstance(store, instance);
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
        public static extern IntPtr wasmtime_component_linker_define_unknown_imports_as_traps(
            Handle linker, Component.Handle component);

        [DllImport(Engine.LibraryName)]
        public static extern IntPtr wasmtime_component_linker_add_wasip2(Handle linker);

        [DllImport(Engine.LibraryName)]
        public static extern void wasmtime_component_linker_delete(IntPtr /* wasmtime_component_linker_t* */ linker);
    }
}