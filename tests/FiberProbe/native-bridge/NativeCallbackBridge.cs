using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Wasmtime.Components;

namespace Wasmtime.FiberProbe;

/// <summary>An async host import. Arguments are copied before Wasmtime frees them.</summary>
internal delegate Task<ComponentValue[]> AsyncComponentCallback(
    ComponentValue[] arguments, CancellationToken cancellation);

/// <summary>
/// Experimental bridge that keeps managed host callbacks off Wasmtime fibers. Native
/// stubs run on the fiber and hand work to a pool of native worker threads that enter
/// the CLR through an <see cref="UnmanagedCallersOnlyAttribute"/> handler.
/// </summary>
internal static unsafe class NativeCallbackBridge
{
    private const string Library = "wasmtime_callback_bridge";
    private const int JobSync = 1, JobAsyncStart = 2, JobAsyncCancel = 3, JobRelease = 4;
    private static readonly object gate = new();
    private static readonly ConcurrentDictionary<IntPtr, Exception> causes = new();
    private static readonly ConcurrentDictionary<IntPtr, CancellationTokenSource> inflight = new();
    private static IntPtr contextGc;
    private static bool initialized;
    [ThreadStatic] private static (IntPtr Job, IntPtr Context)? current;

    internal static bool Enabled =>
        Environment.GetEnvironmentVariable("WASMTIME_CALLBACK_BRIDGE_EXPERIMENT") == "1";

    /// <summary>Demonstrates the hazard the owner-call proxy fixes. Never enable in tests.</summary>
    private static bool UnsafeDirectStoreAccess =>
        Environment.GetEnvironmentVariable("WASMTIME_BRIDGE_UNSAFE_DIRECT_STORE_ACCESS") == "1";

    internal static int MaxWorkers =>
        int.Parse(Environment.GetEnvironmentVariable("WASMTIME_BRIDGE_MAX_WORKERS") ?? "64");

    private sealed record Registration(
        string Name, ComponentFunctionCallback? Callback, AsyncComponentCallback? AsyncCallback);

    internal static void DefineFunction(
        ComponentLinkerInstance root, string name, ComponentFunctionCallback callback)
    {
        if (!Enabled)
        {
            root.DefineFunction(name, callback);
            return;
        }
        Register(root, name, new Registration(name, callback, null));
    }

    internal static void DefineAsyncFunction(
        ComponentLinkerInstance root, string name, AsyncComponentCallback callback)
    {
        if (!Enabled)
        {
            throw new InvalidOperationException("Async host imports require the callback bridge experiment.");
        }
        Register(root, name, new Registration(name, null, callback));
    }

    private static void Register(ComponentLinkerInstance root, string name, Registration registration)
    {
        EnsureInitialized();
        var bytes = Encoding.UTF8.GetBytes(name);
        var isAsync = registration.AsyncCallback is not null;
        var managed = GCHandle.Alloc(registration);
        var env = bridge_register(GCHandle.ToIntPtr(managed), isAsync ? 1 : 0);
        if (env == IntPtr.Zero)
        {
            managed.Free();
            throw new OutOfMemoryException("Native callback bridge allocation failed.");
        }
        IntPtr error;
        try
        {
            var callback = isAsync ? bridge_async_callback() : bridge_sync_callback();
            var finalizer = bridge_registration_finalizer();
            // From here Wasmtime owns env and always runs the finalizer, even on error.
            error = isAsync
                ? wasmtime_component_linker_instance_add_func_async(
                    root.NativeHandle, bytes, (nuint)bytes.Length, callback, env, finalizer)
                : wasmtime_component_linker_instance_add_func(
                    root.NativeHandle, bytes, (nuint)bytes.Length, callback, env, finalizer);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException
            or BadImageFormatException or ObjectDisposedException)
        {
            bridge_registration_abandon(env);
            managed.Free();
            throw;
        }
        if (error != IntPtr.Zero)
        {
            throw WasmtimeException.FromOwnedError(error);
        }
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
            contextGc = NativeLibrary.GetExport(wasmtime, "wasmtime_context_gc");
            delegate* unmanaged[Cdecl]<IntPtr, void> handler = &Handle;
            if (bridge_init((IntPtr)handler, NativeLibrary.GetExport(wasmtime, "wasmtime_error_new"),
                    NativeLibrary.GetExport(wasmtime, "wasmtime_component_val_delete"),
                    ComponentValueMarshaller.ValueSize, (nuint)MaxWorkers) == 0)
            {
                throw new InvalidOperationException("Native callback bridge initialization failed.");
            }
            initialized = true;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void Handle(IntPtr job)
    {
        try
        {
            bridge_job(job, out var kind, out var managed, out var context, out var args, out var nargs,
                out var results, out var nresults, out var request);
            switch (kind)
            {
                case JobSync:
                    RunSync(job, (Registration)GCHandle.FromIntPtr(managed).Target!, context, args,
                        checked((int)nargs), results, checked((int)nresults));
                    break;
                case JobAsyncStart:
                    StartAsync(job, (Registration)GCHandle.FromIntPtr(managed).Target!, context, args,
                        checked((int)nargs), results, checked((int)nresults), request);
                    break;
                case JobAsyncCancel:
                    if (inflight.TryGetValue(request, out var cancellation))
                    {
                        cancellation.Cancel();
                    }
                    bridge_job_done(job);
                    break;
                case JobRelease:
                    GCHandle.FromIntPtr(managed).Free();
                    bridge_registration_free(job);
                    bridge_job_done(job);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown bridge job {kind}.");
            }
        }
        catch (Exception error)
        {
            // A stranded native stub cannot be unwound safely.
            Environment.FailFast("Native callback bridge worker failed.", error);
        }
    }

    private static void RunSync(IntPtr job, Registration registration, IntPtr context,
        IntPtr args, int nargs, IntPtr results, int nresults)
    {
        var previous = current;
        current = (job, context);
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
        }
        var cause = Function.CallbackErrorCause;
        Function.CallbackErrorCause = null;
        if (error != IntPtr.Zero && cause is not null)
        {
            causes[context] = cause;
        }
        bridge_sync_complete(job, error);
    }

    private static void StartAsync(IntPtr job, Registration registration, IntPtr context,
        IntPtr args, int nargs, IntPtr staged, int nresults, IntPtr request)
    {
        Task<ComponentValue[]> task;
        var cancellation = new CancellationTokenSource();
        try
        {
            // Wasmtime frees the arguments once the stub returns, so copy them now.
            var arguments = new ComponentValue[nargs];
            for (var i = 0; i < nargs; i++)
            {
                arguments[i] = ComponentValueMarshaller.Read(args + (i * ComponentValueMarshaller.ValueSize));
            }
            inflight[request] = cancellation;
            task = registration.AsyncCallback!(arguments, cancellation.Token);
            if (task.IsFaulted || task.IsCanceled)
            {
                task.GetAwaiter().GetResult();
            }
        }
        catch (Exception exception)
        {
            inflight.TryRemove(request, out _);
            causes[context] = exception;
            bridge_async_started(job, ComponentLinkerInstance.Native.wasmtime_error_new(exception.Message));
            return;
        }
        bridge_async_started(job, IntPtr.Zero);
        task.ContinueWith(completed => FinishAsync(completed, request, context, staged, nresults),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private static void FinishAsync(Task<ComponentValue[]> task, IntPtr request, IntPtr context,
        IntPtr staged, int nresults)
    {
        inflight.TryRemove(request, out _);
        Exception? failure = null;
        var written = 0;
        try
        {
            var values = task.GetAwaiter().GetResult();
            if (values.Length != nresults)
            {
                throw new InvalidOperationException($"Async callback returned {values.Length} of {nresults} results.");
            }
            for (; written < nresults; written++)
            {
                ComponentValueMarshaller.WriteOwned(values[written],
                    staged + (written * ComponentValueMarshaller.ValueSize));
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            for (var i = 0; i < written; i++)
            {
                wasmtime_component_val_delete(staged + (i * ComponentValueMarshaller.ValueSize));
            }
        }
        if (failure is not null)
        {
            causes[context] = failure;
        }
        if (!bridge_async_finish(request, failure is null) && failure is not null)
        {
            causes.TryRemove(new(context, failure));
        }
    }

    /// <summary>
    /// Runs Store garbage collection. From a bridged callback for the same Store, the
    /// collection is executed by the native stub on the Wasmtime activation that owns it.
    /// </summary>
    internal static void CollectGuest(Store store)
    {
        if (!Enabled || UnsafeDirectStoreAccess || current is not { } active ||
            active.Context != store.Context.handle)
        {
            store.GC();
            return;
        }
        var error = bridge_owner_call(active.Job, contextGc);
        GC.KeepAlive(store);
        if (error != IntPtr.Zero)
        {
            throw WasmtimeException.FromOwnedError(error);
        }
    }

    /// <summary>Returns the managed exception that failed the last bridged callback for a Store.</summary>
    internal static Exception? TakeCause(Store store) =>
        causes.TryRemove(store.Context.handle, out var cause) ? cause : null;

    internal static ulong Counter(BridgeCounter counter) => bridge_counter((int)counter);

    internal static void Finish()
    {
        if (!Enabled || !initialized)
        {
            return;
        }
        // Registration finalizers and cancellation notices are asynchronous jobs.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (Counter(BridgeCounter.LiveRegistrations) != 0 || Counter(BridgeCounter.LiveAsync) != 0)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new InvalidOperationException(
                    $"Callback bridge leaked: registrations={Counter(BridgeCounter.LiveRegistrations)}, " +
                    $"async={Counter(BridgeCounter.LiveAsync)}.");
            }
            Thread.Sleep(10);
        }
        bridge_shutdown();
        causes.Clear();
    }

    internal enum BridgeCounter
    {
        LiveRegistrations, CompletedRequests, PeakBusyWorkers, Exhaustions, OwnerOperations, LiveAsync,
        PeakLiveAsync, AsyncCancelled, StagedDeleted, UnreportableFailures, ThreadsStarted
    }

    [DllImport("wasmtime", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr wasmtime_component_linker_instance_add_func(
        ComponentLinkerInstance.Handle handle, byte[] name, nuint length,
        IntPtr callback, IntPtr env, IntPtr finalizer);
    [DllImport("wasmtime", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr wasmtime_component_linker_instance_add_func_async(
        ComponentLinkerInstance.Handle handle, byte[] name, nuint length,
        IntPtr callback, IntPtr env, IntPtr finalizer);
    [DllImport("wasmtime", CallingConvention = CallingConvention.Cdecl)]
    private static extern void wasmtime_component_val_delete(IntPtr value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern int bridge_init(IntPtr handler, IntPtr errorNew, IntPtr valueDelete,
        nuint valueSize, nuint maxWorkers);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr bridge_register(IntPtr managed, int isAsync);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bridge_registration_abandon(IntPtr registration);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bridge_registration_free(IntPtr job);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr bridge_sync_callback();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr bridge_async_callback();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr bridge_registration_finalizer();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bridge_job(IntPtr job, out int kind, out IntPtr managed, out IntPtr context,
        out IntPtr args, out nuint nargs, out IntPtr results, out nuint nresults, out IntPtr request);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bridge_sync_complete(IntPtr job, IntPtr error);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr bridge_owner_call(IntPtr job, IntPtr operation);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bridge_async_started(IntPtr job, IntPtr error);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool bridge_async_finish(IntPtr request, [MarshalAs(UnmanagedType.I1)] bool succeeded);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bridge_job_done(IntPtr job);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bridge_shutdown();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong bridge_counter(int which);
}
