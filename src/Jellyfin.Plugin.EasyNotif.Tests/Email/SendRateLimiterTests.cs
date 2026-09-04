using Jellyfin.Plugin.EasyNotif.Email;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Email;

/// <summary>
/// Covers <see cref="SendRateLimiter"/> with an injected clock and delay: the first call passes
/// straight through, a call too soon after the previous one waits the remaining interval, and the
/// wait is computed from the shared last-send instant so every caller is spaced apart (R13).
/// </summary>
public class SendRateLimiterTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    [Fact]
    public async Task WaitAsync_FirstCall_DoesNotDelay()
    {
        var clock = new FakeClock();
        var delays = new List<TimeSpan>();
        using var limiter = new SendRateLimiter(Interval, clock.Now, (d, _) => { delays.Add(d); return Task.CompletedTask; });

        await limiter.WaitAsync(CancellationToken.None);

        Assert.Empty(delays);
    }

    [Fact]
    public async Task WaitAsync_SecondCallImmediately_WaitsTheRemainingInterval()
    {
        var clock = new FakeClock();
        var delays = new List<TimeSpan>();
        using var limiter = new SendRateLimiter(Interval, clock.Now, (d, _) => { delays.Add(d); return Task.CompletedTask; });

        await limiter.WaitAsync(CancellationToken.None);
        await limiter.WaitAsync(CancellationToken.None);

        Assert.Single(delays);
        Assert.Equal(Interval, delays[0]);
    }

    [Fact]
    public async Task WaitAsync_SecondCallAfterTheInterval_DoesNotDelay()
    {
        var clock = new FakeClock();
        var delays = new List<TimeSpan>();
        using var limiter = new SendRateLimiter(Interval, clock.Now, (d, _) => { delays.Add(d); return Task.CompletedTask; });

        await limiter.WaitAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(600));
        await limiter.WaitAsync(CancellationToken.None);

        Assert.Empty(delays);
    }

    [Fact]
    public async Task WaitAsync_FiveCallsInARow_DelaysFourTimes()
    {
        var clock = new FakeClock();
        var delays = new List<TimeSpan>();
        using var limiter = new SendRateLimiter(
            Interval,
            clock.Now,
            (d, _) =>
            {
                delays.Add(d);
                clock.Advance(d);
                return Task.CompletedTask;
            });

        for (var i = 0; i < 5; i++)
        {
            await limiter.WaitAsync(CancellationToken.None);
        }

        Assert.Equal(4, delays.Count);
        Assert.All(delays, d => Assert.True(d > TimeSpan.Zero && d <= Interval));
    }

    private sealed class FakeClock
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public Func<DateTimeOffset> Now => () => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
