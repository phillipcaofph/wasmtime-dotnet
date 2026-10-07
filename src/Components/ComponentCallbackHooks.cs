using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wasmtime.Components;

/// <summary>
/// Experimental extension points for a dispatcher that runs component host callbacks
/// away from Wasmtime fibers. Every hook is null by default, in which case the component
/// call paths behave exactly as they do without a dispatcher.
/// </summary>
internal static class ComponentCallbackHooks
{
    /// <summary>
    /// Removes and returns the managed exception recorded for a failed host callback in
    /// the Store context, or null.
    /// </summary>
    internal static Func<IntPtr, Exception?>? TakeHostFailure;

    /// <summary>
    /// Completes when a pending asynchronous call on the Store context may be able to make
    /// progress. Implementations must also complete after a short fallback delay, because
    /// Wasmtime futures can become ready without a host callback finishing.
    /// </summary>
    internal static Func<IntPtr, CancellationToken, Task>? WaitForProgress;

    /// <summary>
    /// True while an isolated asynchronous host callback started by the Store context is still running.
    /// </summary>
    internal static Func<IntPtr, bool>? HasPendingHostWork;

    internal static bool HostWorkPending(IntPtr context) => HasPendingHostWork?.Invoke(context) ?? false;

    /// <summary>
    /// Called when a poll loop starts driving an asynchronous call on the Store context. The
    /// loop calls <see cref="IOwnerOperationServer.Serve"/> between polls, when Wasmtime does
    /// not borrow the Store, and disposes the server when it stops polling.
    /// </summary>
    internal static Func<IntPtr, IOwnerOperationServer>? BeginPolling;

    /// <summary>
    /// When non-null, Store operations from a context the dispatcher is serving off-thread
    /// are rejected.
    /// </summary>
    internal static AsyncLocal<IntPtr>? ServingContext;

    /// <summary>
    /// Called when a Store context is released, or before a top-level component operation
    /// starts, so per-context state cannot leak or be seen by a later Store at the same address.
    /// </summary>
    internal static Action<IntPtr>? ResetContext;

    internal static void Reset(IntPtr context)
    {
        if (context != IntPtr.Zero)
        {
            ResetContext?.Invoke(context);
        }
    }

    /// <summary>
    /// Called on the caller's thread when a top-level component operation starts and ends, so
    /// isolated callbacks can run in the caller's <see cref="ExecutionContext"/>.
    /// </summary>
    internal static Action<IntPtr>? OperationStarted;

    internal static Action<IntPtr>? OperationEnded;

    internal static void BeginOperation(IntPtr context)
    {
        Reset(context);
        if (context != IntPtr.Zero)
        {
            OperationStarted?.Invoke(context);
        }
    }

    internal static void EndOperation(IntPtr context)
    {
        if (context != IntPtr.Zero)
        {
            OperationEnded?.Invoke(context);
        }
    }

    internal static WasmtimeException AttachCause(WasmtimeException error, IntPtr context)
    {
        // Always take the cause so it cannot be reported by a later, unrelated call.
        var take = TakeHostFailure;
        if (take is null || take(context) is not { } cause || error.InnerException is not null)
        {
            return error;
        }

        return error.WithCause(cause);
    }

    /// <summary>
    /// Throws when a host callback failed after its asynchronous call suspended. Wasmtime's C
    /// API cannot report such failures, so a call may otherwise appear to succeed.
    /// </summary>
    internal static void ThrowIfHostFailed(IntPtr context)
    {
        if (TakeHostFailure?.Invoke(context) is { } cause)
        {
            throw new WasmtimeException(
                $"A host import failed after the call suspended: {cause.Message}", cause);
        }
    }

    internal static Task WaitAsync(IntPtr context, CancellationToken cancellationToken) =>
        WaitForProgress is { } wait
            ? wait(context, cancellationToken)
            : Task.Delay(1, cancellationToken);

    internal static void ThrowIfServing(IntPtr context)
    {
        if (ServingContext is { } serving && serving.Value == context && context != IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "This Store cannot be used directly from an isolated host callback. Only Fuel, GC() and " +
                "SetEpochDeadline are available, and only while the component call that started the " +
                "callback is still running.");
        }
    }

    /// <summary>
    /// Runs a Store operation on the Wasmtime activation that owns the Store when the caller
    /// is an isolated host callback serving that Store. Returns false when the caller should
    /// perform the operation directly.
    /// </summary>
    internal static OwnerOperationHook? OwnerOperation;

    internal delegate bool OwnerOperationHook(IntPtr context, StoreOperation operation, ref ulong value);

    internal static bool TryOwnerOperation(IntPtr context, StoreOperation operation, ref ulong value) =>
        OwnerOperation is { } hook && context != IntPtr.Zero && hook(context, operation, ref value);
}

/// <summary>Runs Store operations queued by isolated callbacks on the thread driving a call.</summary>
internal interface IOwnerOperationServer : IDisposable
{
    void Serve();
}

/// <summary>Store operations an isolated host callback may perform. Values match the native bridge.</summary>
internal enum StoreOperation
{
    GC = 0,
    GetFuel = 1,
    SetFuel = 2,
    SetEpochDeadline = 3,
}
