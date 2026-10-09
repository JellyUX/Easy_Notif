using Jellyfin.Plugin.EasyNotif.Webhooks;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Webhooks;

/// <summary>
/// Covers <see cref="WebhookReplayGuard"/>: an id is accepted once, refused again inside the
/// 10-minute window, and accepted again once the window has passed.
/// </summary>
public sealed class WebhookReplayGuardTests
{
    private readonly MovableTime _time = new(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void TryRecord_SameIdTwice_RefusesTheSecond()
    {
        var guard = new WebhookReplayGuard(_time);

        Assert.True(guard.TryRecord("msg_1"));
        Assert.False(guard.TryRecord("msg_1"));
        Assert.True(guard.TryRecord("msg_2"));
    }

    [Fact]
    public void TryRecord_AfterTenMinutes_AcceptsTheIdAgain()
    {
        var guard = new WebhookReplayGuard(_time);
        guard.TryRecord("msg_1");

        _time.Now = _time.Now.AddMinutes(9);
        Assert.False(guard.TryRecord("msg_1"));

        _time.Now = _time.Now.AddMinutes(1);
        Assert.True(guard.TryRecord("msg_1"));
    }

    private sealed class MovableTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
