using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Wasmtime.Components;

/// <summary>
/// Decides how an asynchronous component call waits between polls of its native future.
/// </summary>
/// <remarks>
/// <para>
/// Wasmtime's C API has no waker: <c>wasmtime_call_future_poll</c> reports that a call is not
/// ready but cannot say when it will be, so the binding must choose when to poll again.
/// Isolated asynchronous host callbacks signal their completion, so most waits end early. A
/// wait that ends without a signal doubles the next delay, from <see cref="MinDelay"/> up to
/// <see cref="MaxDelay"/>, so a call that is pending for a reason the binding cannot observe
/// costs a few wake-ups instead of one every millisecond. Any signal or progress resets it.
/// </para>
/// <para>
/// A guest that suspended at an epoch or fuel yield point can resume at once, so it is re-polled
/// after a thread-pool hop instead of waiting. A pending poll cannot tell such a yield from any
/// other wait, so a hop counts as progress only when the guest ran: it consumed fuel, or the
/// poll took at least <see cref="ProgressThreshold"/> (a resumed guest runs until its next
/// deadline). After <see cref="MaxIdleHops"/> hops without progress the call waits instead, so
/// a misclassified wait cannot keep a thread busy.
/// </para>
/// <para>
/// A waker in Wasmtime's C API would let a pending call be resumed exactly when it can make
/// progress, and would make this heuristic unnecessary.
/// </para>
/// </remarks>
internal struct PollBackoff
{
    internal static readonly TimeSpan MinDelay = TimeSpan.FromMilliseconds(1);
    internal static readonly TimeSpan MaxDelay = TimeSpan.FromMilliseconds(50);
    internal static readonly TimeSpan ProgressThreshold = TimeSpan.FromTicks(500); // 50 µs
    internal const int MaxIdleHops = 16;

    private static readonly long progressThresholdTimestamp =
        (long)(ProgressThreshold.TotalSeconds * Stopwatch.Frequency);

    private TimeSpan delay;
    private int idleHops;

    /// <summary>How long the next wait may last before the call is polled again.</summary>
    internal TimeSpan Delay => delay == TimeSpan.Zero ? MinDelay : delay;

    /// <summary>True when a poll that took <paramref name="elapsedTimestamp"/> ran guest code.</summary>
    internal static bool RanGuest(long elapsedTimestamp) => elapsedTimestamp >= progressThresholdTimestamp;

    /// <summary>
    /// Called after a pending poll of a call that may have suspended at a yield point. Returns
    /// true to re-poll after a thread-pool hop, or false to wait for <see cref="Delay"/>.
    /// </summary>
    internal bool ShouldHop(bool progressed)
    {
        if (progressed)
        {
            idleHops = 0;
            delay = MinDelay;
            return true;
        }

        return ++idleHops <= MaxIdleHops;
    }

    /// <summary>Waits without a completion signal; the result is always false (not signalled).</summary>
    internal static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        return false;
    }

    /// <summary>Called after waiting; <paramref name="signalled"/> is false when the wait timed out.</summary>
    internal void OnWaited(bool signalled)
    {
        idleHops = 0;
        delay = signalled ? MinDelay : TimeSpan.FromTicks(Math.Min(Delay.Ticks * 2, MaxDelay.Ticks));
    }
}
