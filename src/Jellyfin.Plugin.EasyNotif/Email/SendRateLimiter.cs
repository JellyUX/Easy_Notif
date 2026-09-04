namespace Jellyfin.Plugin.EasyNotif.Email;

/// <summary>
/// Serializes provider calls so no two leave less than a fixed interval apart. Resend's API limit
/// is a few requests per second across the whole API; the plugin keeps a conservative 500 ms
/// (2 req/s, Synthese.md section 3.2). Registered as a singleton so every caller shares the one
/// gate (R13).
/// </summary>
public sealed class SendRateLimiter : IDisposable
{
    /// <summary>The minimum spacing between two provider calls.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(500);

    private readonly TimeSpan _minInterval;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastSend = DateTimeOffset.MinValue;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="SendRateLimiter"/> class.</summary>
    public SendRateLimiter()
        : this(DefaultInterval, () => DateTimeOffset.UtcNow, Task.Delay)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SendRateLimiter"/> class with an injected clock
    /// and delay, for tests.
    /// </summary>
    /// <param name="minInterval">The minimum spacing between calls.</param>
    /// <param name="now">The clock.</param>
    /// <param name="delay">The delay function.</param>
    internal SendRateLimiter(TimeSpan minInterval, Func<DateTimeOffset> now, Func<TimeSpan, CancellationToken, Task> delay)
    {
        _minInterval = minInterval;
        _now = now;
        _delay = delay;
    }

    /// <summary>
    /// Waits until at least the minimum interval has elapsed since the previous call returned, then
    /// records "now" as the new reference point.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the caller may proceed.</returns>
    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wait = _lastSend + _minInterval - _now();
            if (wait > TimeSpan.Zero)
            {
                await _delay(wait, cancellationToken).ConfigureAwait(false);
            }

            _lastSend = _now();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _gate.Dispose();
        _disposed = true;
    }
}
