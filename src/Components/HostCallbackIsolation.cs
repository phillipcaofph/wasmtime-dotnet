using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wasmtime.Components;

/// <summary>
/// An asynchronous host implementation of a component function.
/// </summary>
/// <param name="arguments">A copy of the arguments passed by the guest.</param>
/// <param name="cancellationToken">Cancelled when Wasmtime drops the pending call.</param>
/// <returns>The results, one per component function result (an empty array for none).</returns>
/// <remarks>
/// The <see cref="Store"/> that called the function cannot be used from the callback or any
/// of its continuations.
/// </remarks>
public delegate Task<ComponentValue[]> ComponentAsyncFunctionCallback(
    ComponentValue[] arguments,
    CancellationToken cancellationToken);

/// <summary>
/// Controls host callback isolation, which runs component host functions on dedicated native
/// worker threads instead of the Wasmtime fiber stacks used by asynchronous component calls.
/// </summary>
/// <remarks>
/// <para>
/// Asynchronous component calls run guest code on stacks the .NET runtime does not know
/// about. A garbage collection that suspends a thread while it runs managed code on such a
/// stack can crash the process. With isolation enabled through
/// <see cref="ComponentLinker.IsolateHostCallbacks"/>, managed host code only ever runs on
/// ordinary threads, and the guest's stack waits in native code.
/// </para>
/// <para>
/// Isolation requires .NET 8 or later and the <c>wasmtime_callback_bridge</c> native library,
/// which the package ships next to Wasmtime for each supported runtime identifier.
/// </para>
/// </remarks>
public static class HostCallbackIsolation
{
    /// <summary>
    /// Gets whether host callback isolation is available in this process.
    /// </summary>
    /// <remarks>
    /// False on .NET Standard targets, when the native library is missing, or when this
    /// assembly is loaded into a collectible <c>AssemblyLoadContext</c>.
    /// </remarks>
    public static bool IsSupported => HostCallbackDispatcher.IsSupported;

    /// <summary>
    /// Gets or sets the maximum number of native worker threads (1 to 256, default 64).
    /// </summary>
    /// <remarks>
    /// Each concurrently running isolated callback, including nested calls back into the
    /// guest, occupies one worker. When the limit is reached a new call traps instead of
    /// waiting, because waiting could deadlock. The value cannot change after the first
    /// isolated function is defined.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 1 to 256.</exception>
    /// <exception cref="InvalidOperationException">An isolated function was already defined.</exception>
    public static int MaxWorkerThreads
    {
        get => HostCallbackDispatcher.MaxWorkers;
        set => HostCallbackDispatcher.MaxWorkers = value;
    }

    /// <summary>
    /// Gets or sets how long a thread handing a call across busy-waits for the other side
    /// before sleeping (0 to 1 ms, default 20 microseconds).
    /// </summary>
    /// <remarks>
    /// Spinning avoids an operating system wake-up per call, which cuts the overhead of a
    /// synchronous isolated call several times over, at the cost of briefly burning CPU on
    /// the calling and worker threads. Set <see cref="TimeSpan.Zero"/> to always sleep. May be
    /// changed at any time.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or above 1 ms.</exception>
    public static TimeSpan SpinDuration
    {
        get => TimeSpan.FromTicks((long)(HostCallbackDispatcher.SpinNanoseconds / 100));
        set
        {
            if (value < TimeSpan.Zero || value > TimeSpan.FromMilliseconds(1))
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Expected between zero and 1 ms.");
            }

            HostCallbackDispatcher.SpinNanoseconds = (ulong)value.Ticks * 100;
        }
    }
}
