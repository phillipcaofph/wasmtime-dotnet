using System;
using System.Runtime.InteropServices;

namespace Wasmtime.Components;

internal static unsafe partial class HostCallbackDispatcher
{
    /// <summary>
    /// Installs an epoch deadline callback that runs on a bridge worker, so no managed code runs on
    /// the Wasmtime stack that reached the deadline. Returns false when the loaded bridge library
    /// predates isolated epoch callbacks.
    /// </summary>
    internal static bool SetEpochDeadlineCallback(Store.Handle store, Store.EpochDeadlineCallback callback)
    {
        ThrowIfUnsupported();
        EnsureInitialized();
        IntPtr function;
        try
        {
            function = bridge_epoch_callback();
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }

        var managed = GCHandle.Alloc(new Registration(callback));
        var env = bridge_register(GCHandle.ToIntPtr(managed), 0);
        if (env == IntPtr.Zero)
        {
            managed.Free();
            throw new OutOfMemoryException("Epoch deadline callback registration failed.");
        }

        try
        {
            // From here Wasmtime owns env and runs the finalizer when the callback is replaced or
            // the store is deleted.
            wasmtime_store_epoch_deadline_callback(store, function, env, bridge_registration_finalizer());
        }
        catch (ObjectDisposedException)
        {
            bridge_registration_abandon(env);
            managed.Free();
            throw;
        }

        return true;
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr bridge_epoch_callback();

    [DllImport(Engine.LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void wasmtime_store_epoch_deadline_callback(Store.Handle store, IntPtr callback,
        IntPtr data, IntPtr finalizer);
}
