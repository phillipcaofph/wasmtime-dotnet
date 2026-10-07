using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
#if NET5_0_OR_GREATER
using System.Runtime.Loader;
#else
// The dispatcher needs UnmanagedCallersOnly and NativeLibrary; on .NET Standard it is a stub.
#pragma warning disable CS0169, CS0649
#endif

namespace Wasmtime.Components;

/// <summary>
/// Runs component host callbacks on native worker threads instead of Wasmtime fiber stacks.
/// </summary>
/// <remarks>
/// Native stubs in <c>wasmtime_callback_bridge</c> run on the fiber, never enter the CLR and
/// hand each call to a pool of native worker threads. Workers enter managed code through an
/// <c>UnmanagedCallersOnly</c> handler, so the CLR never walks or suspends a fiber stack.
/// </remarks>
internal static unsafe class HostCallbackDispatcher
{
    internal const string Library = "wasmtime_callback_bridge";
    private const int JobSync = 1, JobAsyncStart = 2, JobAsyncCancel = 3, JobRelease = 4;
    private const int FinishFailed = 0, FinishOk = 1, FinishFailedPoisoned = 2;
    private const byte ValTypeBool = 0;
    private static readonly TimeSpan PendingFallback = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan IdlePoll = TimeSpan.FromMilliseconds(1);
    private static readonly object gate = new();
    private static readonly ConcurrentDictionary<IntPtr, Exception> causes = new();
    private static readonly ConcurrentDictionary<IntPtr, CancellationTokenSource> inflight = new();
    private static readonly ConcurrentDictionary<IntPtr, ProgressSignal> signals = new();
    private static readonly ConcurrentDictionary<IntPtr, ExecutionContext> flows = new();
    private static readonly AsyncLocal<IntPtr> serving = new();
    private static readonly AsyncLocal<PollSession?> session = new();
    private static bool? supported;
    private static bool initialized;
    private static int maxWorkers = 64;
    [ThreadStatic] private static (IntPtr Job, IntPtr Context)? current;
    [ThreadStatic] private static bool workerNamed;

    /// <summary>Test hook: false restores fixed 1 ms polling of pending async calls.</summary>
    internal static bool UseProgressSignals = true;

    /// <summary>How long a waiting thread busy-polls before sleeping, in nanoseconds.</summary>
    internal static ulong SpinNanoseconds
    {
        get => Volatile.Read(ref spinNanoseconds);
        set
        {
            lock (gate)
            {
                spinNanoseconds = value;
#if NET5_0_OR_GREATER
                if (initialized)
                {
                    bridge_set_spin(value);
                }
#endif
            }
        }
    }

    private static ulong spinNanoseconds = 20_000;

    private sealed class Registration
    {
        public Registration(string name, ComponentFunctionCallback? callback,
            ComponentAsyncFunctionCallback? asyncCallback)
        {
            Name = name;
            Callback = callback;
            AsyncCallback = asyncCallback;
        }

        public Registration(Store.EpochDeadlineCallback epochCallback)
        {
            Name = "epoch deadline callback";
            EpochCallback = epochCallback;
        }

        public string Name { get; }
        public ComponentFunctionCallback? Callback { get; }
        public ComponentAsyncFunctionCallback? AsyncCallback { get; }
        public Store.EpochDeadlineCallback? EpochCallback { get; }
    }

    private sealed class ProgressSignal : IOwnerOperationServer
    {
        private readonly IntPtr context;
        private readonly Queue<DeferredOperation> deferred = new();
        private int pollers;
        private int generation;

        public ProgressSignal(IntPtr context) => this.context = context;

        public readonly SemaphoreSlim Ready = new(0);
        public int Pending;

        public ProgressSignal BeginPolling()
        {
            lock (deferred)
            {
                if (pollers++ == 0)
                {
                    generation++;
                }
            }

            return this;
        }

        public PollSession CurrentSession(IntPtr store)
        {
            lock (deferred)
            {
                return new PollSession(store, this, pollers == 0 ? -1 : generation);
            }
        }

        /// <summary>
        /// Queues an operation for the poll loop driving this Store. Returns false when the poll
        /// session that started the callback has ended, in which case nothing owns the Store on
        /// the callback's behalf.
        /// </summary>
        public bool TryDefer(DeferredOperation operation, int sessionGeneration)
        {
            lock (deferred)
            {
                if (pollers == 0 || generation != sessionGeneration)
                {
                    return false;
                }

                deferred.Enqueue(operation);
            }

            Ready.Release();
            return true;
        }

        public void Serve()
        {
            while (true)
            {
                DeferredOperation operation;
                lock (deferred)
                {
                    if (deferred.Count == 0)
                    {
                        return;
                    }

                    operation = deferred.Dequeue();
                }

                operation.Run(context);
            }
        }

        public void Dispose()
        {
            List<DeferredOperation>? abandoned = null;
            lock (deferred)
            {
                if (--pollers == 0 && deferred.Count > 0)
                {
                    abandoned = new List<DeferredOperation>(deferred);
                    deferred.Clear();
                }
            }

            if (abandoned is not null)
            {
                foreach (var operation in abandoned)
                {
                    operation.Fail(new InvalidOperationException(
                        "The component call that started this isolated asynchronous host callback has finished, " +
                        "so the callback can no longer use its Store."));
                }
            }
        }
    }

    /// <summary>The poll session of the component call that started an asynchronous callback.</summary>
    private sealed class PollSession
    {
        public PollSession(IntPtr context, ProgressSignal signal, int generation)
        {
            Context = context;
            Signal = signal;
            Generation = generation;
        }

        public IntPtr Context { get; }
        public ProgressSignal Signal { get; }
        public int Generation { get; }
    }

    /// <summary>A Store operation from an asynchronous callback that has already yielded.</summary>
    private sealed class DeferredOperation
    {
        private readonly StoreOperation operation;
        private readonly ManualResetEventSlim done = new(false);
        private Exception? error;

        public DeferredOperation(StoreOperation operation, ulong value)
        {
            this.operation = operation;
            Value = value;
        }

        public ulong Value { get; private set; }

        public void Run(IntPtr context)
        {
            try
            {
                var store = new StoreContext(context);
                switch (operation)
                {
                    case StoreOperation.GetFuel:
                        Value = store.GetFuel();
                        break;
                    case StoreOperation.SetFuel:
                        store.SetFuel(Value);
                        break;
                    case StoreOperation.GC:
                        store.GC();
                        break;
                    case StoreOperation.SetEpochDeadline:
                        store.SetEpochDeadline(Value);
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown Store operation {operation}.");
                }
            }
            catch (Exception exception)
            {
                error = exception;
            }

            done.Set();
        }

        public void Fail(Exception exception)
        {
            error = exception;
            done.Set();
        }

        public ulong Wait()
        {
            done.Wait();
            done.Dispose();
            if (error is not null)
            {
                ExceptionDispatchInfo.Capture(error).Throw();
            }

            return Value;
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

    private static bool Probe()
    {
#if NET5_0_OR_GREATER
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
#else
        return false;
#endif
    }

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

#if NET5_0_OR_GREATER
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
            var callback = isAsync ? bridge_async_callback() : bridge_sync_callback();
            error = isAsync
                ? wasmtime_component_linker_instance_add_func_async(
                    instance, bytes, (nuint)bytes.Length, callback, env, bridge_registration_finalizer())
                : wasmtime_component_linker_instance_add_func(
                    instance, bytes, (nuint)bytes.Length, callback, env, bridge_registration_finalizer());
        }
        catch (Exception exception) when (exception is ObjectDisposedException or EntryPointNotFoundException)
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

            if (initialization == 0 ||
                bridge_set_store_op((int)StoreOperation.GC, NativeLibrary.GetExport(wasmtime, "wasmtime_context_gc")) == 0 ||
                bridge_set_store_op((int)StoreOperation.GetFuel, NativeLibrary.GetExport(wasmtime, "wasmtime_context_get_fuel")) == 0 ||
                bridge_set_store_op((int)StoreOperation.SetFuel, NativeLibrary.GetExport(wasmtime, "wasmtime_context_set_fuel")) == 0 ||
                bridge_set_store_op((int)StoreOperation.SetEpochDeadline,
                    NativeLibrary.GetExport(wasmtime, "wasmtime_context_set_epoch_deadline")) == 0)
            {
                throw new InvalidOperationException("Host callback isolation failed to initialize.");
            }

            bridge_set_spin(spinNanoseconds);
            ComponentCallbackHooks.TakeHostFailure =
                context => causes.TryRemove(context, out var cause) ? cause : null;
            ComponentCallbackHooks.ServingContext = serving;
            ComponentCallbackHooks.OwnerOperation = TryOwnerOperation;
            ComponentCallbackHooks.HasPendingHostWork =
                context => signals.TryGetValue(context, out var signal) && Volatile.Read(ref signal.Pending) > 0;
            ComponentCallbackHooks.ResetContext = context =>
            {
                causes.TryRemove(context, out _);
                signals.TryRemove(context, out _);
                flows.TryRemove(context, out _);
            };
            ComponentCallbackHooks.OperationStarted = context =>
            {
                // Null when the caller suppressed flow; callbacks then run in an empty context.
                if (ExecutionContext.Capture() is { } flow)
                {
                    flows[context] = flow;
                }
            };
            ComponentCallbackHooks.OperationEnded = context => flows.TryRemove(context, out _);
            ComponentCallbackHooks.BeginPolling =
                context => signals.GetOrAdd(context, key => new ProgressSignal(key)).BeginPolling();
            if (UseProgressSignals)
            {
                ComponentCallbackHooks.WaitForProgress = WaitForProgress;
            }

            initialized = true;
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void Handle(IntPtr job)
    {
        // The worker's own (empty) context. Restoring it after every job keeps one callback's
        // ambient state, such as AsyncLocal values or culture, from leaking into the next.
        var baseline = ExecutionContext.Capture();
        try
        {
            if (!workerNamed)
            {
                workerNamed = true;
                Thread.CurrentThread.Name ??= "Wasmtime host callback";
            }

            bridge_job(job, out var kind, out var managed, out var context, out var type, out var args,
                out var nargs, out var results, out var nresults, out var request);
            if ((kind == JobSync || kind == JobAsyncStart) && flows.TryGetValue(context, out var flow))
            {
                // Callbacks see the ExecutionContext of the call that reached them, as they would
                // on the direct path.
                ExecutionContext.Restore(flow);
            }

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
                    if (inflight.TryGetValue(request, out var cancellation))
                    {
                        try
                        {
                            cancellation.Cancel();
                        }
                        catch (AggregateException)
                        {
                            // Thrown by the callback's own cancellation registrations; the callback
                            // still observes the cancellation, and the worker must not fail the process.
                        }
                    }

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
        finally
        {
            if (baseline is not null)
            {
                ExecutionContext.Restore(baseline);
            }
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
            error = registration.EpochCallback is { } epochCallback
                ? Store.InvokeEpochDeadlineCallback(epochCallback, context, (ulong*)results)
                : ComponentLinkerInstance.Invoke(registration.Callback!, registration.Name,
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

    private static bool ResultIsBool(IntPtr type, int nresults)
    {
        if (nresults != 1)
        {
            return false;
        }

        var buffer = stackalloc byte[64];
        if (!ComponentFunction.Native.wasmtime_component_func_type_result(type, (IntPtr)buffer))
        {
            return false;
        }

        var kind = buffer[0];
        ComponentFunction.Native.wasmtime_component_valtype_delete((IntPtr)buffer);
        return kind == ValTypeBool;
    }

    private static void StartAsync(IntPtr job, Registration registration, IntPtr context, IntPtr type,
        IntPtr args, int nargs, IntPtr staged, int nresults, IntPtr request)
    {
        Task<ComponentValue[]> task;
        var cancellation = new CancellationTokenSource();
        var signal = signals.GetOrAdd(context, key => new ProgressSignal(key));
        // The function type is only valid while the native stub is blocked.
        var poisonBool = ResultIsBool(type, nresults);
        var previousServing = serving.Value;
        var previousSession = session.Value;
        var previous = current;
        try
        {
            // Wasmtime frees the arguments once the stub returns, so copy them now.
            var arguments = new ComponentValue[nargs];
            for (var i = 0; i < nargs; i++)
            {
                arguments[i] = ComponentValueMarshaller.Read(args + (i * ComponentValueMarshaller.ValueSize));
            }

            inflight[request] = cancellation;
            // Flows into the callback's continuations, so direct Store use is rejected after awaits too.
            serving.Value = context;
            // Binds later owner operations to the poll session driving this call, so a callback that
            // outlives it cannot act through a later call on the same Store.
            session.Value = signal.CurrentSession(context);
            // Owner operations are only possible until the callback returns its task: the stub is
            // blocked on the Store's thread until then.
            current = (job, context);
            task = registration.AsyncCallback!(arguments, cancellation.Token)
                ?? throw new InvalidOperationException($"Async host function '{registration.Name}' returned a null task.");
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
        finally
        {
            current = previous;
            serving.Value = previousServing;
            session.Value = previousSession;
        }

        Interlocked.Increment(ref signal.Pending);
        bridge_async_started(job, IntPtr.Zero);
        task.ContinueWith(
            completed => FinishAsync(completed, request, context, staged, nresults, poisonBool, signal),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private static void FinishAsync(Task<ComponentValue[]> task, IntPtr request, IntPtr context,
        IntPtr staged, int nresults, bool poisonBool, ProgressSignal signal)
    {
        // Not disposed: a concurrent cancel job may still hold the source. It owns no timer or
        // linked registration, so leaving it to the GC releases nothing early.
        inflight.TryRemove(request, out _);

        Exception? failure = null;
        var written = 0;
        try
        {
            var values = task.GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("Async host function returned null results.");
            if (values.Length != nresults)
            {
                throw new InvalidOperationException(
                    $"Async host function returned {values.Length} result(s) but {nresults} were expected.");
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

        var outcome = FinishOk;
        if (failure is not null)
        {
            causes[context] = failure;
            outcome = FinishFailed;
            if (poisonBool)
            {
                // Wasmtime's placeholder is bool(false), which would type-check; an s32 traps instead.
                ComponentValueMarshaller.WriteOwned(ComponentValue.S32(0), staged);
                outcome = FinishFailedPoisoned;
            }
        }

        if (!bridge_async_finish(request, outcome) && failure is not null)
        {
            causes.TryRemove(new(context, failure));
        }

        Interlocked.Decrement(ref signal.Pending);
        signal.Ready.Release();
    }

    private static Task WaitForProgress(IntPtr context, CancellationToken cancellation)
    {
        if (!signals.TryGetValue(context, out var signal))
        {
            return Task.Delay(IdlePoll, cancellation);
        }

        // With an isolated request outstanding, completion is signalled; otherwise keep the 1 ms poll.
        return signal.Ready.WaitAsync(
            Volatile.Read(ref signal.Pending) > 0 ? PendingFallback : IdlePoll, cancellation);
    }

    private static bool TryOwnerOperation(IntPtr context, StoreOperation operation, ref ulong value)
    {
        if (current is not { } active || active.Context != context)
        {
            // An asynchronous callback that already yielded: the poll loop driving the call owns
            // the Store between polls and runs the operation there.
            if (serving.Value != context || session.Value is not { } owner || owner.Context != context)
            {
                return false;
            }

            var deferred = new DeferredOperation(operation, value);
            if (!owner.Signal.TryDefer(deferred, owner.Generation))
            {
                return false;
            }

            value = deferred.Wait();
            return true;
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

    internal static ulong Counter(Counters counter) => initialized ? bridge_counter((int)counter) : 0;

    /// <summary>Host failures recorded but not yet consumed by a component call.</summary>
    internal static int PendingCauses => causes.Count;

    internal static bool IsContextTracked(IntPtr context) => signals.ContainsKey(context);

    internal static void Shutdown()
    {
        if (initialized)
        {
            bridge_shutdown();
        }
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
    private static extern int bridge_set_store_op(int op, IntPtr function);

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
    private static extern IntPtr bridge_async_callback();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr bridge_epoch_callback();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr bridge_registration_finalizer();

    [DllImport("wasmtime", CallingConvention = CallingConvention.Cdecl)]
    private static extern void wasmtime_store_epoch_deadline_callback(Store.Handle store, IntPtr callback,
        IntPtr data, IntPtr finalizer);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bridge_job(IntPtr job, out int kind, out IntPtr managed, out IntPtr context,
        out IntPtr type, out IntPtr args, out nuint nargs, out IntPtr results, out nuint nresults,
        out IntPtr request);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bridge_sync_complete(IntPtr job, IntPtr error);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr bridge_owner_call(IntPtr job, int op, IntPtr argument);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bridge_async_started(IntPtr job, IntPtr error);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool bridge_async_finish(IntPtr request, int outcome);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bridge_job_done(IntPtr job);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bridge_shutdown();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong bridge_counter(int which);
#else
    internal static bool SetEpochDeadlineCallback(Store.Handle store, Store.EpochDeadlineCallback callback) =>
        false;

    private static void Register(ComponentLinkerInstance.Handle instance, string name, Registration registration) =>
        ThrowIfUnsupported();

    internal static ulong Counter(Counters counter) => 0;

    internal static int PendingCauses => causes.Count;

    internal static bool IsContextTracked(IntPtr context) => false;

    internal static void Shutdown()
    {
    }
#endif

    internal enum Counters
    {
        LiveRegistrations, CompletedRequests, PeakBusyWorkers, Exhaustions, OwnerOperations, LiveAsync,
        PeakLiveAsync, AsyncCancelled, StagedDeleted, LateFailures, ThreadsStarted, PoisonedResults,
    }
}
