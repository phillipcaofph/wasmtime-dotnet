using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Wasmtime.Components;

namespace Wasmtime.FiberProbe;

internal static class NativeCallbackBridge
{
    private const string Library = "wasmtime_callback_bridge";
    private static readonly ConcurrentBag<(IntPtr Bridge, Thread Dispatcher)> dispatchers = new();
    internal static bool Enabled =>
        Environment.GetEnvironmentVariable("WASMTIME_CALLBACK_BRIDGE_EXPERIMENT") == "1";

    internal static void DefineFunction(
        ComponentLinkerInstance root, string name, ComponentFunctionCallback callback)
    {
        if (!Enabled)
        {
            root.DefineFunction(name, callback);
            return;
        }

        var handle = root.NativeHandle;
        var bytes = Encoding.UTF8.GetBytes(name);
        var bridge = bridge_create();
        if (bridge == IntPtr.Zero)
        {
            throw new OutOfMemoryException("Native callback bridge allocation failed.");
        }

        var dispatcher = new Thread(() =>
        {
            try
            {
                while (true)
                {
                    var request = bridge_wait(bridge);
                    if (request == IntPtr.Zero)
                    {
                        return;
                    }
                    bridge_arguments(request, out var args, out var nargs, out var results, out var nresults);
                    var error = ComponentLinkerInstance.Invoke(
                        callback, name, args, checked((int)nargs), results, checked((int)nresults));
                    // The prototype transfers the native error, not the poller's thread-local cause.
                    Function.CallbackErrorCause = null;
                    bridge_complete(bridge, request, error);
                }
            }
            catch (Exception error)
            {
                // A stranded native callback cannot safely recover or be unwound.
                Environment.FailFast("Native callback dispatcher failed.", error);
            }
        })
        {
            IsBackground = true,
            Name = "Wasmtime callback bridge"
        };

        var finalizer = bridge_finalizer();
        try
        {
            dispatcher.Start();
        }
        catch
        {
            Marshal.GetDelegateForFunctionPointer<Finalizer>(finalizer)(bridge);
            bridge_destroy(bridge);
            throw;
        }
        dispatchers.Add((bridge, dispatcher));
        var transferred = false;
        try
        {
            // Resolve the entry point before transferring finalizer ownership to Wasmtime.
            var nativeCallback = bridge_callback();
            transferred = true;
            var error = wasmtime_component_linker_instance_add_func(
                handle, bytes, (nuint)bytes.Length, nativeCallback, bridge, finalizer);
            if (error != IntPtr.Zero)
            {
                throw WasmtimeException.FromOwnedError(error);
            }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            transferred = false;
            throw;
        }
        finally
        {
            if (!transferred)
            {
                Marshal.GetDelegateForFunctionPointer<Finalizer>(finalizer)(bridge);
            }
        }
    }

    internal static void Finish()
    {
        while (dispatchers.TryTake(out var item))
        {
            if (!item.Dispatcher.Join(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("Callback bridge registration survived Store/linker disposal.");
            }
            bridge_destroy(item.Bridge);
        }
        if (Enabled && bridge_live() != 0)
        {
            throw new InvalidOperationException("Native callback bridges leaked.");
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Finalizer(IntPtr env);

    [DllImport("wasmtime", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr wasmtime_component_linker_instance_add_func(
        ComponentLinkerInstance.Handle handle, byte[] name, nuint length,
        IntPtr callback, IntPtr env, IntPtr finalizer);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr bridge_create();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr bridge_callback();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr bridge_finalizer();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr bridge_wait(IntPtr bridge);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bridge_arguments(
        IntPtr request, out IntPtr args, out nuint nargs, out IntPtr results, out nuint nresults);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bridge_complete(IntPtr bridge, IntPtr request, IntPtr error);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bridge_destroy(IntPtr bridge);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ulong bridge_live();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ulong bridge_completed();
}
