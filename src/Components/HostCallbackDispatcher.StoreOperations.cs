using System;
using System.Runtime.InteropServices;

namespace Wasmtime.Components;

internal static unsafe partial class HostCallbackDispatcher
{
    /// <summary>The isolated callback this worker is running, while its native stub is blocked.</summary>
    [ThreadStatic] private static (IntPtr Job, IntPtr Context)? current;

    /// <summary>
    /// Runs a Store operation on the Wasmtime activation that owns the Store when the caller
    /// is an isolated host callback serving that Store. Returns false when the caller should
    /// perform the operation directly.
    /// </summary>
    internal static bool TryOwnerOperation(IntPtr context, StoreOperation operation, ref ulong value)
    {
        if (!initialized || context == IntPtr.Zero)
        {
            return false;
        }

        if (current is not { } active || active.Context != context)
        {
            return TryDeferOwnerOperation(context, operation, ref value);
        }

        var argument = value;
        var error = bridge_owner_call(active.Job, (int)operation, (IntPtr)(&argument));
        if (error != IntPtr.Zero)
        {
            throw WasmtimeException.FromOwnedError(error);
        }

        value = argument;
        return true;
    }

    private static bool ConfigureStoreOperations(IntPtr wasmtime) =>
        SetStoreOperation(wasmtime, StoreOperation.GC, "wasmtime_context_gc") &&
        SetStoreOperation(wasmtime, StoreOperation.GetFuel, "wasmtime_context_get_fuel") &&
        SetStoreOperation(wasmtime, StoreOperation.SetFuel, "wasmtime_context_set_fuel") &&
        SetStoreOperation(wasmtime, StoreOperation.SetEpochDeadline, "wasmtime_context_set_epoch_deadline");

#if NET5_0_OR_GREATER
    private static bool SetStoreOperation(IntPtr wasmtime, StoreOperation operation, string export) =>
        bridge_set_store_op((int)operation, NativeLibrary.GetExport(wasmtime, export)) != 0;
#else
    private static bool SetStoreOperation(IntPtr wasmtime, StoreOperation operation, string export) => false;
#endif

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int bridge_set_store_op(int op, IntPtr function);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr bridge_owner_call(IntPtr job, int op, IntPtr argument);
}

/// <summary>Store operations an isolated host callback may perform. Values match the native bridge.</summary>
internal enum StoreOperation
{
    GC = 0,
    GetFuel = 1,
    SetFuel = 2,
    SetEpochDeadline = 3,
}
