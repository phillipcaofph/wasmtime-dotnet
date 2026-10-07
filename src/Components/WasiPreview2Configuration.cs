using System;

namespace Wasmtime.Components;

/// <summary>
/// Configures host implementations for WASI Preview 2 interfaces.
/// </summary>
public sealed class WasiPreview2Configuration
{
    internal Func<DateTimeOffset>? WallClock { get; private set; }
    internal TimeSpan WallClockResolution { get; private set; } = TimeSpan.FromTicks(1);

    /// <summary>
    /// Replaces <c>wasi:clocks/wall-clock</c> with a host-provided clock.
    /// </summary>
    /// <param name="clock">Returns the current wall-clock time.</param>
    /// <returns>The current configuration.</returns>
    public WasiPreview2Configuration WithWallClock(Func<DateTimeOffset> clock)
    {
        return WithWallClock(clock, TimeSpan.FromTicks(1));
    }

    /// <summary>
    /// Replaces <c>wasi:clocks/wall-clock</c> with a host-provided clock and resolution.
    /// </summary>
    /// <param name="clock">Returns the current wall-clock time.</param>
    /// <param name="resolution">The smallest interval distinguishable by the clock.</param>
    /// <returns>The current configuration.</returns>
    public WasiPreview2Configuration WithWallClock(Func<DateTimeOffset> clock, TimeSpan resolution)
    {
        if (clock is null)
        {
            throw new ArgumentNullException(nameof(clock));
        }

        if (resolution <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(resolution));
        }

        WallClock = clock;
        WallClockResolution = resolution;
        return this;
    }
}
