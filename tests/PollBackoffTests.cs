using System;
using System.Diagnostics;
using FluentAssertions;
using Wasmtime.Components;
using Xunit;

namespace Wasmtime.Tests
{
    public sealed class PollBackoffTests
    {
        [Fact]
        public void ItDoublesTheDelayAfterEachUnsignalledWaitUpToTheMaximum()
        {
            var backoff = new PollBackoff();
            backoff.Delay.Should().Be(PollBackoff.MinDelay);

            var expected = PollBackoff.MinDelay;
            for (var i = 0; i < 10; i++)
            {
                backoff.OnWaited(signalled: false);
                expected = TimeSpan.FromTicks(Math.Min(expected.Ticks * 2, PollBackoff.MaxDelay.Ticks));
                backoff.Delay.Should().Be(expected);
            }

            backoff.Delay.Should().Be(PollBackoff.MaxDelay);
        }

        [Fact]
        public void ItResetsTheDelayWhenSignalled()
        {
            var backoff = new PollBackoff();
            backoff.OnWaited(signalled: false);
            backoff.OnWaited(signalled: false);

            backoff.OnWaited(signalled: true);

            backoff.Delay.Should().Be(PollBackoff.MinDelay);
        }

        [Fact]
        public void ItResetsTheDelayWhenTheGuestMakesProgress()
        {
            var backoff = new PollBackoff();
            backoff.OnWaited(signalled: false);
            backoff.OnWaited(signalled: false);

            backoff.ShouldHop(progressed: true).Should().BeTrue();

            backoff.Delay.Should().Be(PollBackoff.MinDelay);
        }

        [Fact]
        public void ItAlwaysHopsWhileTheGuestMakesProgress()
        {
            var backoff = new PollBackoff();

            for (var i = 0; i < PollBackoff.MaxIdleHops * 10; i++)
            {
                backoff.ShouldHop(progressed: true).Should().BeTrue();
            }
        }

        [Fact]
        public void ItBoundsHopsWithoutProgress()
        {
            var backoff = new PollBackoff();

            for (var i = 0; i < PollBackoff.MaxIdleHops; i++)
            {
                backoff.ShouldHop(progressed: false).Should().BeTrue();
            }

            backoff.ShouldHop(progressed: false).Should().BeFalse();

            // After waiting, a misclassified wait gets another bounded burst, at a longer delay.
            backoff.OnWaited(signalled: false);
            backoff.Delay.Should().BeGreaterThan(PollBackoff.MinDelay);
            backoff.ShouldHop(progressed: false).Should().BeTrue();
        }

        [Fact]
        public void ItTreatsOnlyLongPollsAsGuestProgress()
        {
            var threshold = (long)(PollBackoff.ProgressThreshold.TotalSeconds * Stopwatch.Frequency);

            PollBackoff.RanGuest(0).Should().BeFalse();
            PollBackoff.RanGuest(threshold - 1).Should().BeFalse();
            PollBackoff.RanGuest(threshold).Should().BeTrue();
        }
    }
}
