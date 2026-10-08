using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
#if NET5_0_OR_GREATER
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
#endif

namespace Wasmtime.Components;

/// <summary>
/// Runs component host callbacks on native worker threads instead of Wasmtime fiber stacks.
/// </summary>
/// <remarks>
/// Native stubs in <c>wasmtime_callback_bridge</c> run on the fiber, never enter the CLR and
/// hand each call to a pool of native worker threads. Workers enter managed code through an
/// <c>UnmanagedCallersOnly</c> handler, so the CLR never walks or suspends a fiber stack.
/// Until the first isolated function is defined, every hook used by <see cref="Store"/> and
/// the component call paths is a no-op.
/// </remarks>
internal static unsafe partial class HostCallbackDispatcher
{
    internal const string Library = "wasmtime_callback_bridge";
    private const int JobSync = 1, JobAsyncStart = 2, JobAsyncCancel = 3, JobRelease = 4;
    private static readonly object gate = new();
    private static readonly ConcurrentDictionary<IntPtr, Exception> causes = new();
    private static readonly AsyncLocal<IntPtr> serving = new();
    private static bool? supported;
#pragma warning disable CS0649 // Only the .NET 5+ dispatcher can initialize.
    private static volatile bool initialized;
#pragma warning restore CS0649
    private static int maxWorkers = 64;
    private static ulong spinNanoseconds = 20_000;

    /// <summary>How long a waiting thread busy-polls before sleeping, in nanoseconds.</summary>
    internal static ulong SpinNanoseconds
    {
        get => Volatile.Read(ref spinNanoseconds);
        set
        {
            lock (gate)
            {
                spinNanoseconds = value;
                if (initialized)
                {
                    bridge_set_spin(value);
                }
            }
        }
    }

    internal static bool IsSupported
    {
        get
        {
            lock (gate)
            {
                return supported ??= Probe();
            }
        }
    }

    internal static int MaxWorkers
    {
        get => Volatile.Read(ref maxWorkers);
        set
        {
            if (value < 1 || value > 256)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Expected between 1 and 256 worker threads.");
            }

            lock (gate)
            {
                if (initialized)
                {
                    throw new InvalidOperationException(
                        "The worker thread limit cannot change after an isolated host callback was defined.");
                }

                maxWorkers = value;
            }
        }
    }

    /// <summary>Host failures recorded but not yet consumed by a component call.</summary>
    internal static int PendingCauses => causes.Count;

    internal static void ThrowIfUnsupported()
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException(
                "Host callback isolation requires .NET 8 or later, a non-collectible AssemblyLoadContext and the " +
                $"'{Library}' native library for this platform.");
        }
    }

    internal static void DefineFunction(ComponentLinkerInstance.Handle instance, string name,
        ComponentFunctionCallback callback) =>
        Register(instance, name, new Registration(name, callback, null));

    internal static void DefineAsyncFunction(ComponentLinkerInstance.Handle instance, string name,
        ComponentAsyncFunctionCallback callback) =>
        Register(instance, name, new Registration(name, null, callback));

    /// <summary>
    /// Attaches the managed exception recorded for a failed isolated callback on the Store
    /// context as the cause of <paramref name="error"/>.
    /// </summary>
    internal static WasmtimeException AttachCause(WasmtimeException error, IntPtr context)
    {
        // Always take the cause so it cannot be reported by a later, unrelated call.
        if (!initialized || !causes.TryRemove(context, out var cause) || error.InnerException is not null)
        {
            return error;
        }

        return error.WithCause(cause);
    }

    /// <summary>
    /// Rejects direct use of a Store from an isolated callback it started: the Store's own
    /// thread is blocked in native code, waiting for the callback.
    /// </summary>
    internal static void ThrowIfServing(IntPtr context)
    {
        if (initialized && context != IntPtr.Zero && serving.Value == context)
        {
            throw new InvalidOperationException(
                "This Store cannot be used directly from an isolated host callback. Only Fuel, GC() and " +
                "SetEpochDeadline are available, and only while the component call that started the " +
                "callback is still running.");
        }
    }

    /// <summary>
    /// Called before a top-level component operation starts on a Store context.
    /// </summary>
    internal static void BeginOperation(IntPtr context) => ReleaseContext(context);

    /// <summary>
    /// Forgets per-context state when a Store is disposed, or before an operation starts, so it
    /// cannot leak or be seen by a later Store at the same address.
    /// </summary>
    internal static void ReleaseContext(IntPtr context)
    {
        if (initialized && context != IntPtr.Zero)
        {
            causes.TryRemove(context, out _);
            signals.TryRemove(context, out _);
        }
    }

    private sealed class Registration
    {
        public Registration(string name, ComponentFunctionCallback? callback,
            ComponentAsyncFunctionCallback? asyncCallback)
        {
            Name = name;
            Callback = callback;
            AsyncCallback = asyncCallback;
        }

        public string Name { get; }
        public ComponentFunctionCallback? Callback { get; }
        public ComponentAsyncFunctionCallback? AsyncCallback { get; }
    }

    private static void Register(ComponentLinkerInstance.Handle instance, string name, Registration registration)
    {
        ThrowIfUnsupported();
        EnsureInitialized();
        var bytes = Encoding.UTF8.GetBytes(name);
        var isAsync = registration.AsyncCallback is not null;
        var managed = GCHandle.Alloc(registration);
        var env = bridge_register(GCHandle.ToIntPtr(managed), isAsync ? 1 : 0);
        if (env == IntPtr.Zero)
        {
            managed.Free();
            throw new OutOfMemoryException("Host callback registration failed.");
        }

        IntPtr error;
        try
        {
            // From here Wasmtime owns env and always runs the finalizer, even on error.
            error = isAsync
                ? wasmtime_component_linker_instance_add_func_async(
                    instance, bytes, (nuint)bytes.Length, bridge_async_callback(), env, bridge_registration_finalizer())
                : wasmtime_component_linker_instance_add_func(
                    instance, bytes, (nuint)bytes.Length, bridge_sync_callback(), env, bridge_registration_finalizer());
        }
        catch (ObjectDisposedException)
        {
            // Marshalling failed before Wasmtime took ownership of env.
            bridge_registration_abandon(env);
            managed.Free();
            throw;
        }

        if (error != IntPtr.Zero)
        {
            throw WasmtimeException.FromOwnedError(error);
        }
    }

    private static void RunSync(IntPtr job, Registration registration, IntPtr context,
        IntPtr args, int nargs, IntPtr results, int nresults)
    {
        var previous = current;
        var previousServing = serving.Value;
        current = (job, context);
        serving.Value = context;
        IntPtr error;
        try
        {
            // Results are written directly: the stub keeps Wasmtime's buffers alive while it waits.
            error = ComponentLinkerInstance.Invoke(registration.Callback!, registration.Name,
                args, nargs, results, nresults);
        }
        finally
        {
            current = previous;
            serving.Value = previousServing;
        }

        var cause = Function.CallbackErrorCause;
        Function.CallbackErrorCause = null;
        if (error != IntPtr.Zero && cause is not null)
        {
            causes[context] = cause;
        }

        bridge_sync_complete(job, error);
    }

#if NET5_0_OR_GREATER
    private static bool Probe()
    {
        // Native workers keep a pointer to this assembly's entry point for the process lifetime.
        if (AssemblyLoadContext.GetLoadContext(typeof(HostCallbackDispatcher).Assembly)?.IsCollectible == true)
        {
            return false;
        }

        if (!NativeLibrary.TryLoad(Library, typeof(HostCallbackDispatcher).Assembly, null, out var bridge))
        {
            return false;
        }

        return NativeLibrary.TryGetExport(bridge, "bridge_set_store_op", out _);
    }

    private static void EnsureInitialized()
    {
        lock (gate)
        {
            if (initialized)
            {
                return;
            }

            var wasmtime = NativeLibrary.Load("wasmtime", typeof(Engine).Assembly, null);
            delegate* unmanaged[Cdecl]<IntPtr, void> handler = &Handle;
            var initialization = bridge_init((IntPtr)handler,
                NativeLibrary.GetExport(wasmtime, "wasmtime_error_new"),
                NativeLibrary.GetExport(wasmtime, "wasmtime_component_val_delete"),
                ComponentValueMarshaller.ValueSize, (nuint)maxWorkers);
            if (initialization < 0)
            {
                throw new InvalidOperationException(
                    "Host callback isolation could not start its worker thread.");
            }

            if (initialization == 0 || !ConfigureStoreOperations(wasmtime))
            {
                throw new InvalidOperationException("Host callback isolation failed to initialize.");
            }

            bridge_set_spin(spinNanoseconds);
            initialized = true;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void Handle(IntPtr job)
    {
        try
        {
            if (!workerNamed)
            {
                workerNamed = true;
                Thread.CurrentThread.Name ??= "Wasmtime host callback";
            }

            bridge_job(job, out var kind, out var managed, out var context, out var type, out var args,
                out var nargs, out var results, out var nresults, out var request);
            switch (kind)
            {
                case JobSync:
                    RunSync(job, (Registration)GCHandle.FromIntPtr(managed).Target!, context, args,
                        checked((int)nargs), results, checked((int)nresults));
                    break;
                case JobAsyncStart:
                    StartAsync(job, (Registration)GCHandle.FromIntPtr(managed).Target!, context, type,
                        args, checked((int)nargs), results, checked((int)nresults), request);
                    break;
                case JobAsyncCancel:
                    CancelAsync(request);
                    bridge_job_done(job);
                    break;
                case JobRelease:
                    GCHandle.FromIntPtr(managed).Free();
                    bridge_registration_free(job);
                    bridge_job_done(job);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown host callback job {kind}.");
            }
        }
        catch (Exception error)
        {
            // A native stub blocked on this job cannot be unwound safely.
            Environment.FailFast("Wasmtime host callback worker failed.", error);
        }
    }

    [ThreadStatic] private static bool workerNamed;
#else
    // The dispatcher needs UnmanagedCallersOnly and NativeLibrary, so .NET Standard never isolates.
    private static bool Probe() => false;

    private static void EnsureInitialized() => ThrowIfUnsupported();
#endif

    [DllImport(Engine.LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr wasmtime_component_linker_instance_add_func(
        ComponentLinkerInstance.Handle handle, byte[] name, nuint length,
        IntPtr callback, IntPtr env, IntPtr finalizer);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int bridge_init(IntPtr handler, IntPtr errorNew, IntPtr valueDelete,
        nuint valueSize, nuint maxWorkers);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bridge_set_spin(ulong nanoseconds);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr bridge_register(IntPtr managed, int isAsync);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bridge_registration_abandon(IntPtr registration);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bridge_registration_free(IntPtr job);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr bridge_sync_callback();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr bridge_registration_finalizer();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bridge_job(IntPtr job, out int kind, out IntPtr managed, out IntPtr context,
        out IntPtr type, out IntPtr args, out nuint nargs, out IntPtr results, out nuint nresults,
        out IntPtr request);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bridge_sync_complete(IntPtr job, IntPtr error);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bridge_job_done(IntPtr job);
}
