using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Wasmtime.Components;

/// <summary>
/// An instantiated <see cref="Component"/>.
/// </summary>
/// <remarks>
/// Instances are identified by an integer within their store and have no destructor, so this
/// type is not disposable. Passing an instance to a different <see cref="Store"/> than the one
/// it was created in may abort the process.
/// </remarks>
public class ComponentInstance
{
    internal readonly Native.Instance instance;
    internal readonly Store store;

    internal ComponentInstance(Store store, Native.Instance instance)
    {
        this.store = store;
        this.instance = instance;
    }

    /// <summary>
    /// Looks up an export of this instance by name.
    /// </summary>
    /// <param name="name">The name of the export.</param>
    /// <param name="parent">The instance export to look within, or null for the root.</param>
    /// <returns>The export index, or null if there is no such export.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="name"/> is null.</exception>
    public ComponentExport? GetExport(string name, ComponentExport? parent = null)
    {
        if (name is null)
        {
            throw new ArgumentNullException(nameof(name));
        }

        var nameBytes = Encoding.UTF8.GetBytes(name);

        // The parent is optional, and a null SafeHandle cannot be marshalled, so pass it as a raw pointer.
        var parentSafeHandle = parent?.NativeHandle;
        var parentHandleAddedRef = false;

        try
        {
            parentSafeHandle?.DangerousAddRef(ref parentHandleAddedRef);
            var parentHandle = parentSafeHandle?.DangerousGetHandle() ?? IntPtr.Zero;

            unsafe
            {
                fixed (byte* namePtr = nameBytes)
                {
                    var index = Native.wasmtime_component_instance_get_export_index(
                        in instance,
                        store.Context.handle,
                        parentHandle,
                        namePtr,
                        (nuint)nameBytes.Length);

                    GC.KeepAlive(store);

                    return index == IntPtr.Zero ? null : new ComponentExport(index);
                }
            }
        }
        finally
        {
            if (parentHandleAddedRef)
            {
                parentSafeHandle!.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Gets an exported function by its export index.
    /// </summary>
    /// <param name="export">The export index of the function.</param>
    /// <returns>The function, or null if the export is not a function.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="export"/> is null.</exception>
    public ComponentFunction? GetFunction(ComponentExport export)
    {
        if (export is null)
        {
            throw new ArgumentNullException(nameof(export));
        }

        var found = Native.wasmtime_component_instance_get_func(
            in instance, store.Context.handle, export.NativeHandle, out var func);

        GC.KeepAlive(store);

        return found ? new ComponentFunction(store, func) : null;
    }

    /// <summary>
    /// Gets an exported function from the root of this instance.
    /// </summary>
    /// <param name="name">The name of the function.</param>
    /// <returns>The function, or null if there is no such exported function.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="name"/> is null.</exception>
    public ComponentFunction? GetFunction(string name)
    {
        using var export = GetExport(name);
        return export is null ? null : GetFunction(export);
    }

    /// <summary>
    /// Gets a function exported by one of this instance's exported interfaces.
    /// </summary>
    /// <param name="interfaceName">The name of the interface, such as <c>my:pkg/iface@1.0.0</c>.</param>
    /// <param name="name">The name of the function within the interface.</param>
    /// <returns>The function, or null if there is no such exported function.</returns>
    /// <exception cref="ArgumentNullException">Thrown if an argument is null.</exception>
    public ComponentFunction? GetFunction(string interfaceName, string name)
    {
        if (interfaceName is null)
        {
            throw new ArgumentNullException(nameof(interfaceName));
        }

        if (name is null)
        {
            throw new ArgumentNullException(nameof(name));
        }

        using var instanceExport = GetExport(interfaceName);
        if (instanceExport is null)
        {
            return null;
        }

        using var export = GetExport(name, instanceExport);
        return export is null ? null : GetFunction(export);
    }

    internal static class Native
    {
        /// <summary>Mirrors <c>wasmtime_component_instance_t</c>.</summary>
        [StructLayout(LayoutKind.Explicit, Size = 16)]
        internal struct Instance
        {
            [FieldOffset(0)]
            public ulong StoreId;

            [FieldOffset(8)]
            public uint Private;
        }

        [DllImport(Engine.LibraryName)]
        public static extern unsafe IntPtr wasmtime_component_instance_get_export_index(
            in Instance instance,
            IntPtr context,
            IntPtr instance_export_index,
            byte* name,
            nuint name_len);

        [DllImport(Engine.LibraryName)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool wasmtime_component_instance_get_func(
            in Instance instance,
            IntPtr context,
            ComponentExport.Handle export_index,
            out ComponentFunction.Native.Func func_out);
    }
}