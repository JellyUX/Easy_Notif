namespace Jellyfin.Plugin.EasyNotif.Webhooks;

/// <summary>
/// Remembers the <c>svix-id</c> of every verified webhook for 10 minutes, twice the signature
/// timestamp tolerance, so a captured signed message cannot be replayed while its timestamp is still
/// accepted. Resend retries keep the same id, so a retry of an already handled event is ignored too.
/// In memory only: a restart forgets the ids, which is acceptable because every webhook effect is
/// idempotent.
/// </summary>
public sealed class WebhookReplayGuard
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _time;
    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <summary>Initializes a new instance of the <see cref="WebhookReplayGuard"/> class.</summary>
    /// <param name="time">The clock.</param>
    public WebhookReplayGuard(TimeProvider time) => _time = time;

    /// <summary>
    /// Records <paramref name="id"/> as handled. Call only after the signature is verified, so a
    /// forged id cannot block the genuine message.
    /// </summary>
    /// <param name="id">The <c>svix-id</c> header value.</param>
    /// <returns>True the first time an id is seen within the window; false for a replay.</returns>
    public bool TryRecord(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            foreach (var expired in _seen.Where(p => now - p.Value >= Window).Select(p => p.Key).ToList())
            {
                _seen.Remove(expired);
            }

            return _seen.TryAdd(id, now);
        }
    }
}
