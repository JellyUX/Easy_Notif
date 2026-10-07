using System.Net;
using System.Threading.RateLimiting;

namespace Jellyfin.Plugin.EasyNotif.Unsubscribe;

/// <summary>
/// Per-IP fixed-window limiter for the anonymous <c>/EasyNotif/u/{token}</c> endpoint (audit
/// SEC-04). A plugin cannot add ASP.NET's rate-limiting middleware to Jellyfin's pipeline, so the
/// controller asks this singleton directly. Behind a reverse proxy that Jellyfin does not trust as a
/// known proxy, every request shares the proxy's address and the limit becomes global.
/// </summary>
public sealed class UnsubscribeRateLimiter : IDisposable
{
    /// <summary>Requests allowed per address per window.</summary>
    public const int PermitsPerWindow = 20;

    /// <summary>The window length.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly PartitionedRateLimiter<string> _limiter = PartitionedRateLimiter.Create<string, string>(
        key => RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = PermitsPerWindow,
            Window = Window,
            QueueLimit = 0,
            AutoReplenishment = true
        }));

    /// <summary>Takes one permit for an address.</summary>
    /// <param name="address">The client address, or null when unknown (all unknowns share one bucket).</param>
    /// <returns>True when the request may proceed, false when the address is over the limit.</returns>
    public bool TryAcquire(IPAddress? address)
    {
        using var lease = _limiter.AttemptAcquire(address?.ToString() ?? "unknown");
        return lease.IsAcquired;
    }

    /// <inheritdoc/>
    public void Dispose() => _limiter.Dispose();
}
