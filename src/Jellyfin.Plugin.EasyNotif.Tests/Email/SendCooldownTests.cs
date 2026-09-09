using Jellyfin.Plugin.EasyNotif.Email;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Email;

/// <summary>
/// Covers <see cref="SendCooldown"/>: a user is allowed once, blocked inside the window, allowed
/// again after it elapses, and users are independent.
/// </summary>
public sealed class SendCooldownTests
{
    private DateTimeOffset _now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private SendCooldown Build() => new(TimeSpan.FromMinutes(15), () => _now);

    [Fact]
    public void TryConsume_AllowsTheFirstCall_ThenBlocksInsideTheWindow()
    {
        var cooldown = Build();
        var user = Guid.NewGuid();

        Assert.True(cooldown.TryConsume(user));

        _now = _now.AddMinutes(14);
        Assert.False(cooldown.TryConsume(user));
        Assert.False(cooldown.TryConsume(user));
    }

    [Fact]
    public void TryConsume_AllowsAgainOnceTheWindowHasElapsed()
    {
        var cooldown = Build();
        var user = Guid.NewGuid();

        Assert.True(cooldown.TryConsume(user));
        _now = _now.AddMinutes(15);
        Assert.True(cooldown.TryConsume(user));
    }

    [Fact]
    public void TryConsume_BlockedCallDoesNotPushTheWindowForward()
    {
        var cooldown = Build();
        var user = Guid.NewGuid();

        Assert.True(cooldown.TryConsume(user)); // t=0
        _now = _now.AddMinutes(10);
        Assert.False(cooldown.TryConsume(user)); // blocked, must not reset the clock
        _now = _now.AddMinutes(5); // t=15 from the first allowed call
        Assert.True(cooldown.TryConsume(user));
    }

    [Fact]
    public void TryConsume_IsPerUser()
    {
        var cooldown = Build();

        Assert.True(cooldown.TryConsume(Guid.NewGuid()));
        Assert.True(cooldown.TryConsume(Guid.NewGuid()));
    }
}
