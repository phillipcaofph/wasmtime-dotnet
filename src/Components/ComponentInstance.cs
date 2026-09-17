using System.Runtime.InteropServices;

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

        //[DllImport(Engine.LibraryName)]
        //public static extern IntPtr /* wasmtime_component_export_index_t* */ wasmtime_component_instance_get_export_index (wasmtime_component_instance_t *instance, wasmtime_context_t *context, ComponentExport.Handle instance_export_index, string name, nuint name_len)

        // [DllImport(Engine.LibraryName)]
        //todo: bool 	wasmtime_component_instance_get_func (const wasmtime_component_instance_t *instance, wasmtime_context_t *context, const wasmtime_component_export_index_t *export_index, wasmtime_component_func_t *func_out)
    }
}