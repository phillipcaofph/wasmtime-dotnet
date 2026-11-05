using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Wasmtime.Components;

/// <summary>
/// An owned <c>wasmtime_component_resource_type_t*</c>, released with
/// <c>wasmtime_component_resource_type_delete</c>.
/// </summary>
internal sealed class SafeResourceTypeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeResourceTypeHandle(IntPtr handle)
        : base(true)
    {
        SetHandle(handle);
    }

    /// <summary>
    /// Compares two resource types with <c>wasmtime_component_resource_type_equal</c>. Pure: it
    /// touches no Store.
    /// </summary>
    public bool NativeEquals(SafeResourceTypeHandle other) =>
        ComponentResourceNative.wasmtime_component_resource_type_equal(this, other);

    protected override bool ReleaseHandle()
    {
        ComponentResourceNative.wasmtime_component_resource_type_delete(handle);
        return true;
    }
}

/// <summary>
/// P/Invokes for component resources (<c>component/val.h</c>, <c>component/types/resource.h</c>),
/// resource definitions in a linker instance (<c>component/linker.h</c>) and the value type
/// accessors used to find resource positions in function types (<c>component/types/val.h</c>).
/// </summary>
/// <remarks>
/// Functions taking a <c>wasmtime_context_t*</c> may only be called with a context the caller legally
/// holds: top-level on the thread using the Store, or inside a host callback with the context Wasmtime
/// passed to it. Every other function here is pure and touches no Store.
/// </remarks>
internal static class ComponentResourceNative
{
    /// <summary>The <c>wasmtime_component_valtype_t</c> kind of an <c>own</c> handle.</summary>
    public const byte ValTypeOwn = 21;

    /// <summary>The <c>wasmtime_component_valtype_t</c> kind of a <c>borrow</c> handle.</summary>
    public const byte ValTypeBorrow = 22;

    public const byte ValTypeList = 13;
    public const byte ValTypeRecord = 14;
    public const byte ValTypeTuple = 15;
    public const byte ValTypeVariant = 16;
    public const byte ValTypeOption = 18;
    public const byte ValTypeResult = 19;
    public const byte ValTypeFuture = 23;
    public const byte ValTypeStream = 24;
    public const byte ValTypeErrorContext = 25;
    public const byte ValTypeMap = 26;

    /// <summary>Size of <c>wasmtime_component_valtype_t</c>: a 1-byte kind padded to 8, plus an 8-byte union.</summary>
    public const int ValueTypeSize = 16;

    /// <summary>Offset of the <c>of</c> union within <c>wasmtime_component_valtype_t</c>.</summary>
    public const int ValueTypePayloadOffset = 8;

    // ----- wasmtime_component_resource_type_t -----

    [DllImport(Engine.LibraryName)]
    public static extern IntPtr wasmtime_component_resource_type_new_host(uint ty);

    [DllImport(Engine.LibraryName)]
    public static extern IntPtr wasmtime_component_resource_type_clone(IntPtr ty);

    [DllImport(Engine.LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool wasmtime_component_resource_type_equal(
        SafeResourceTypeHandle a, SafeResourceTypeHandle b);

    [DllImport(Engine.LibraryName, EntryPoint = "wasmtime_component_resource_type_equal")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool wasmtime_component_resource_type_equal_raw(SafeResourceTypeHandle a, IntPtr b);

    [DllImport(Engine.LibraryName)]
    public static extern void wasmtime_component_resource_type_delete(IntPtr ty);

    // ----- wasmtime_component_resource_any_t -----

    [DllImport(Engine.LibraryName)]
    public static extern IntPtr wasmtime_component_resource_any_type(IntPtr resource);

    [DllImport(Engine.LibraryName)]
    public static extern IntPtr wasmtime_component_resource_any_clone(IntPtr resource);

    [DllImport(Engine.LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool wasmtime_component_resource_any_owned(IntPtr resource);

    [DllImport(Engine.LibraryName)]
    public static extern IntPtr wasmtime_component_resource_any_drop(IntPtr context, IntPtr resource);

    [DllImport(Engine.LibraryName)]
    public static extern void wasmtime_component_resource_any_delete(IntPtr resource);

    [DllImport(Engine.LibraryName)]
    public static extern IntPtr wasmtime_component_resource_any_to_host(
        IntPtr context, IntPtr resource, out IntPtr host);

    // ----- wasmtime_component_resource_host_t -----

    [DllImport(Engine.LibraryName)]
    public static extern IntPtr wasmtime_component_resource_host_new(
        [MarshalAs(UnmanagedType.I1)] bool owned, uint rep, uint ty);

    [DllImport(Engine.LibraryName)]
    public static extern uint wasmtime_component_resource_host_rep(IntPtr resource);

    [DllImport(Engine.LibraryName)]
    public static extern uint wasmtime_component_resource_host_type(IntPtr resource);

    [DllImport(Engine.LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool wasmtime_component_resource_host_owned(IntPtr resource);

    [DllImport(Engine.LibraryName)]
    public static extern void wasmtime_component_resource_host_delete(IntPtr resource);

    [DllImport(Engine.LibraryName)]
    public static extern IntPtr wasmtime_component_resource_host_to_any(
        IntPtr context, IntPtr resource, out IntPtr any);

    // ----- linker -----

    /// <summary>
    /// Mirrors <c>wasmtime_component_resource_destructor_t</c>: <c>(data, context, rep) -> error</c>.
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr ResourceDestructor(IntPtr data, IntPtr context, uint rep);

    [DllImport(Engine.LibraryName)]
    public static extern unsafe IntPtr wasmtime_component_linker_instance_add_resource(
        ComponentLinkerInstance.Handle linker_instance,
        byte* name,
        nuint name_len,
        SafeResourceTypeHandle resource,
        ResourceDestructor destructor,
        IntPtr data,
        Function.Native.Finalizer? finalizer);

    // ----- function and value types -----

    [DllImport(Engine.LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern unsafe bool wasmtime_component_func_type_param_nth(
        IntPtr ty, nuint nth, out byte* name_ret, out nuint name_len_ret, IntPtr type_ret);

    [DllImport(Engine.LibraryName)]
    public static extern void wasmtime_component_list_type_element(IntPtr ty, IntPtr type_ret);

    [DllImport(Engine.LibraryName)]
    public static extern nuint wasmtime_component_record_type_field_count(IntPtr ty);

    [DllImport(Engine.LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern unsafe bool wasmtime_component_record_type_field_nth(
        IntPtr ty, nuint nth, out byte* name_ret, out nuint name_len_ret, IntPtr type_ret);

    [DllImport(Engine.LibraryName)]
    public static extern nuint wasmtime_component_tuple_type_types_count(IntPtr ty);

    [DllImport(Engine.LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool wasmtime_component_tuple_type_types_nth(IntPtr ty, nuint nth, IntPtr type_ret);

    [DllImport(Engine.LibraryName)]
    public static extern nuint wasmtime_component_variant_type_case_count(IntPtr ty);

    [DllImport(Engine.LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern unsafe bool wasmtime_component_variant_type_case_nth(
        IntPtr ty, nuint nth, out byte* name_ret, out nuint name_len_ret,
        [MarshalAs(UnmanagedType.I1)] out bool has_payload_ret, IntPtr payload_ret);

    [DllImport(Engine.LibraryName)]
    public static extern void wasmtime_component_option_type_ty(IntPtr ty, IntPtr type_ret);

    [DllImport(Engine.LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool wasmtime_component_result_type_ok(IntPtr ty, IntPtr type_ret);

    [DllImport(Engine.LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool wasmtime_component_result_type_err(IntPtr ty, IntPtr type_ret);

    [DllImport(Engine.LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool wasmtime_component_future_type_ty(IntPtr ty, IntPtr type_ret);

    [DllImport(Engine.LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern bool wasmtime_component_stream_type_ty(IntPtr ty, IntPtr type_ret);

    [DllImport(Engine.LibraryName)]
    public static extern void wasmtime_component_map_type_key(IntPtr ty, IntPtr type_ret);

    [DllImport(Engine.LibraryName)]
    public static extern void wasmtime_component_map_type_value(IntPtr ty, IntPtr type_ret);

    [DllImport(Engine.LibraryName)]
    public static extern nuint wasmtime_component_enum_type_names_count(IntPtr ty);

    [DllImport(Engine.LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern unsafe bool wasmtime_component_enum_type_names_nth(
        IntPtr ty, nuint nth, out byte* name_ret, out nuint name_len_ret);

    [DllImport(Engine.LibraryName)]
    public static extern nuint wasmtime_component_flags_type_names_count(IntPtr ty);

    [DllImport(Engine.LibraryName)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static extern unsafe bool wasmtime_component_flags_type_names_nth(
        IntPtr ty, nuint nth, out byte* name_ret, out nuint name_len_ret);

    [DllImport(Engine.LibraryName)]
    public static extern void wasmtime_error_delete(IntPtr error);
}
