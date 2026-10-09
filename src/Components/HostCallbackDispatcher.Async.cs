using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Wasmtime.Components;

internal static unsafe partial class HostCallbackDispatcher
{
    private const int FinishFailed = 0, FinishOk = 1, FinishFailedPoisoned = 2;
    private const byte ValTypeBool = 0;
    private static readonly TimeSpan PendingFallback = TimeSpan.FromMilliseconds(20);
    private static readonly ConcurrentDictionary<IntPtr, CancellationTokenSource> inflight = new();
    private static readonly ConcurrentDictionary<IntPtr, ProgressSignal> signals = new();
    private static readonly AsyncLocal<PollSession?> session = new();

    /// <summary>
    /// Called when a poll loop starts driving an asynchronous call on the Store context. The
    /// loop calls <see cref="ProgressSignal.Serve"/> between polls, when Wasmtime does not
    /// borrow the Store, and disposes the signal when it stops polling.
    /// </summary>
    internal static ProgressSignal? BeginPolling(IntPtr context) =>
        initialized ? signals.GetOrAdd(context, key => new ProgressSignal(key)).BeginPolling() : null;

    /// <summary>
    /// True while an isolated asynchronous host callback started by the Store context is still running.
    /// </summary>
    internal static bool HasPendingWork(IntPtr context) =>
        initialized && signals.TryGetValue(context, out var signal) && Volatile.Read(ref signal.Pending) > 0;

    /// <summary>
    /// Waits until a pending asynchronous call on the Store context may be able to make
    /// progress, or for at most <paramref name="delay"/>, because Wasmtime futures can also become
    /// ready without a host callback finishing. Returns true when an isolated callback signalled.
    /// </summary>
    internal static Task<bool> WaitForProgressAsync(IntPtr context, TimeSpan delay,
        CancellationToken cancellationToken)
    {
        if (!signals.TryGetValue(context, out var signal))
        {
            return PollBackoff.DelayAsync(delay, cancellationToken);
        }

        // Completion of an outstanding isolated request is signalled, so its fallback can be longer.
        var timeout = Volatile.Read(ref signal.Pending) > 0 && PendingFallback > delay ? PendingFallback : delay;
        return signal.Ready.WaitAsync(timeout, cancellationToken);
    }

    /// <summary>
    /// Returns the failure of a host callback that failed after its asynchronous call suspended,
    /// or null. Wasmtime's C API cannot report such failures, so a call may otherwise appear to
    /// succeed.
    /// </summary>
    internal static WasmtimeException? TakeLateHostFailure(IntPtr context) =>
        initialized && causes.TryRemove(context, out var cause)
            ? new WasmtimeException($"A host import failed after the call suspended: {cause.Message}", cause)
            : null;

    internal static bool IsContextTracked(IntPtr context) => signals.ContainsKey(context);

    /// <summary>Tracks the asynchronous callbacks and poll loops of one Store context.</summary>
    internal sealed class ProgressSignal : IDisposable
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

        /// <summary>Runs the Store operations queued by callbacks that have already yielded.</summary>
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
    internal sealed class PollSession
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
    internal sealed class DeferredOperation
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

    /// <summary>
    /// Runs a Store operation from an asynchronous callback that already yielded: the poll loop
    /// driving the call owns the Store between polls and runs it there.
    /// </summary>
    private static bool TryDeferOwnerOperation(IntPtr context, StoreOperation operation, ref ulong value)
    {
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
        Task<ComponentValue[]>? task = null;
        var cancellation = new CancellationTokenSource();
        var signal = signals.GetOrAdd(context, key => new ProgressSignal(key));
        // The function type is only valid while the native stub is blocked.
        var poisonBool = ResultIsBool(type, nresults);
        var previousServing = serving.Value;
        var previousSession = session.Value;
        var previous = current;
        // Failures are handled after the catch block; see ComponentFunction.ThrowIfFailed.
        Exception? startFailure = null;
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
            startFailure = exception;
        }
        finally
        {
            current = previous;
            serving.Value = previousServing;
            session.Value = previousSession;
        }

        if (startFailure is not null)
        {
            inflight.TryRemove(request, out _);
            causes[context] = startFailure;
            bridge_async_started(job, ComponentLinkerInstance.Native.wasmtime_error_new(startFailure.Message));
            return;
        }

        Interlocked.Increment(ref signal.Pending);
        bridge_async_started(job, IntPtr.Zero);
        task!.ContinueWith(
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
        }

        if (failure is not null)
        {
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
            // Wasmtime already dropped the call, so nothing will report the failure.
            ((ICollection<KeyValuePair<IntPtr, Exception>>)causes).Remove(new(context, failure));
        }

        Interlocked.Decrement(ref signal.Pending);
        signal.Ready.Release();
    }

    private static void CancelAsync(IntPtr request)
    {
        if (inflight.TryGetValue(request, out var cancellation))
        {
            try
            {
                cancellation.Cancel();
            }
            catch (AggregateException)
            {
                // Thrown by the callback's own cancellation registrations; the callback still
                // observes the cancellation, and the worker must not fail the process.
            }
        }
    }

    [DllImport(Engine.LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr wasmtime_component_linker_instance_add_func_async(
        ComponentLinkerInstance.Handle handle, byte[] name, nuint length,
        IntPtr callback, IntPtr env, IntPtr finalizer);

    [DllImport(Engine.LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void wasmtime_component_val_delete(IntPtr value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr bridge_async_callback();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern void bridge_async_started(IntPtr job, IntPtr error);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool bridge_async_finish(IntPtr request, int outcome);
}
