using System.Collections.Concurrent;

namespace Jellyfin.Plugin.EasyNotif.Email;

/// <summary>
/// A per-user cooldown for the "send myself a test email" action. <c>POST /EasyNotif/me/test</c> is
/// open to any authenticated Jellyfin user and sends to a self-declared, unverified address, so
/// without this gate one account can loop it and burn the whole Resend daily quota (F-01).
/// Registered as a singleton (the controller is per-request); state is in memory and resets on
/// restart, which is fine - a restart is not an abuse vector.
/// </summary>
public sealed class SendCooldown
{
    /// <summary>The minimum spacing between two test emails for the same user.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(15);

    private readonly TimeSpan _interval;
    private readonly Func<DateTimeOffset> _now;
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _last = new();

    /// <summary>Initializes a new instance of the <see cref="SendCooldown"/> class.</summary>
    public SendCooldown()
        : this(DefaultInterval, () => DateTimeOffset.UtcNow)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SendCooldown"/> class with an injected clock,
    /// for tests.
    /// </summary>
    /// <param name="interval">The per-user cooldown window.</param>
    /// <param name="now">The clock.</param>
    internal SendCooldown(TimeSpan interval, Func<DateTimeOffset> now)
    {
        _interval = interval;
        _now = now;
    }

    /// <summary>
    /// Records "now" for the user and returns whether the action is allowed. Returns <c>false</c>
    /// (and does not update the timestamp) when the previous allowed call was less than the
    /// cooldown window ago.
    /// </summary>
    /// <param name="userId">The Jellyfin user id.</param>
    /// <returns><c>true</c> when the caller may proceed.</returns>
    public bool TryConsume(Guid userId)
    {
        var now = _now();
        var allowed = true;

        _last.AddOrUpdate(
            userId,
            now,
            (_, previous) =>
            {
                if (now - previous < _interval)
                {
                    allowed = false;
                    return previous;
                }

                return now;
            });

        return allowed;
    }
}
