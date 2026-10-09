using System.Globalization;
using Jellyfin.Plugin.EasyNotif.IO;
using Jellyfin.Plugin.EasyNotif.Storage;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EasyNotif.Alerts;

/// <summary>
/// The incidents an administrator should hear about without opening the logs (FEAT-04, DEC-62):
/// the WARN/ERROR events of <see cref="IncidentStore.Types"/>, fed by
/// <see cref="Logging.IEasyNotifLog"/>, kept 7 days in <c>incidents.json</c> with the time the
/// last digest was sent. See <see cref="IncidentStore"/>.
/// </summary>
public interface IIncidentStore
{
    /// <summary>
    /// Records an event when its type is an incident and no <see cref="Suppress"/> scope is active.
    /// Never throws: a write failure is only logged.
    /// </summary>
    /// <param name="eventType">The log event type.</param>
    /// <param name="fields">The log fields, already masked.</param>
    void Record(string eventType, IReadOnlyDictionary<string, object?>? fields);

    /// <summary>Returns the incidents still kept, oldest first.</summary>
    /// <returns>The incidents.</returns>
    IReadOnlyList<Incident> All();

    /// <summary>Gets the UTC time the last digest was accepted by the transport, or null.</summary>
    DateTime? LastDigestUtc { get; }

    /// <summary>Records that a digest was accepted by the transport.</summary>
    /// <param name="sentUtc">The send time (UTC).</param>
    void MarkDigestSent(DateTime sentUtc);

    /// <summary>
    /// Opens a scope in which <see cref="Record"/> ignores everything on the current async flow, so a
    /// failing digest send does not raise an incident about itself.
    /// </summary>
    /// <returns>The scope; dispose it to record again.</returns>
    IDisposable Suppress();
}

/// <summary>On-disk shape of <c>incidents.json</c>.</summary>
public sealed class IncidentFile
{
    /// <summary>Gets or sets the storage schema version.</summary>
    public int Schema { get; set; } = 1;

    /// <summary>Gets or sets the UTC time the last digest was accepted, or null.</summary>
    public DateTime? LastDigestUtc { get; set; }

    /// <summary>Gets the recorded incidents, oldest first.</summary>
    public List<Incident> Incidents { get; init; } = [];
}

/// <summary>One recorded incident.</summary>
/// <param name="Ts">The UTC time it was logged.</param>
/// <param name="Type">The log event type (for example <c>email.failed</c>).</param>
/// <param name="Detail">A short, masked detail (campaign, drain, error), or null.</param>
public sealed record Incident(DateTime Ts, string Type, string? Detail);

/// <inheritdoc cref="IIncidentStore"/>
public sealed class IncidentStore : JsonFileStore<IncidentFile>, IIncidentStore
{
    /// <summary>The event types recorded as incidents: transport, quota, campaign, bounce and drain failures.</summary>
    public static readonly IReadOnlySet<string> Types = new HashSet<string>(StringComparer.Ordinal)
    {
        "email.failed",
        "quota.threshold",
        "quota.suspended",
        "dispatch.error",
        "dispatch.retry-abandoned",
        "series.retry-abandoned",
        "announcement.retry-abandoned",
        "webhook.rejected",
        "webhook.complaint",
        "drain.lost",
        "drain.dropped"
    };

    /// <summary>How long an incident is kept.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private const int MaxIncidents = 500;
    private const int MaxDetailLength = 200;

    // The fields worth showing an administrator, in display order. Never an address or a user id.
    private static readonly string[] DetailFields = ["campaignId", "drain", "count", "reason", "attempts", "subject", "error"];

    private static readonly AsyncLocal<int> SuppressDepth = new();

    private readonly Func<DateTime> _now;

    /// <summary>Initializes a new instance of the <see cref="IncidentStore"/> class.</summary>
    /// <param name="applicationPaths">Provides the application data directory path.</param>
    /// <param name="fileSystem">File system abstraction.</param>
    /// <param name="logger">Logger.</param>
    public IncidentStore(IApplicationPaths applicationPaths, IFileSystem fileSystem, ILogger<IncidentStore> logger)
        : this(applicationPaths, fileSystem, logger, () => DateTime.UtcNow)
    {
    }

    internal IncidentStore(IApplicationPaths applicationPaths, IFileSystem fileSystem, ILogger<IncidentStore> logger, Func<DateTime> now)
        : base(applicationPaths, fileSystem, logger, "incidents.json")
    {
        _now = now;
    }

    /// <inheritdoc/>
    public DateTime? LastDigestUtc => Read(file => file.LastDigestUtc);

    /// <inheritdoc/>
    public void Record(string eventType, IReadOnlyDictionary<string, object?>? fields)
    {
        if (!Types.Contains(eventType) || SuppressDepth.Value > 0)
        {
            return;
        }

        var now = _now();
        var incident = new Incident(now, eventType, Detail(fields));
        try
        {
            Mutate(file =>
            {
                file.Incidents.RemoveAll(i => i.Ts < now - Retention);
                file.Incidents.Add(incident);
                if (file.Incidents.Count > MaxIncidents)
                {
                    file.Incidents.RemoveRange(0, file.Incidents.Count - MaxIncidents);
                }

                return true;
            });
        }
        catch (Exception ex)
        {
            // Called from inside a log call: recording an incident must never break the caller.
            _logger.LogError(ex, "[EasyNotif] Could not record a {Type} incident.", eventType);
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<Incident> All()
    {
        var cutoff = _now() - Retention;
        return Read(file => (IReadOnlyList<Incident>)file.Incidents.Where(i => i.Ts >= cutoff).ToList());
    }

    /// <inheritdoc/>
    public void MarkDigestSent(DateTime sentUtc) => Mutate(file =>
    {
        file.LastDigestUtc = sentUtc;
        return true;
    });

    /// <inheritdoc/>
    public IDisposable Suppress()
    {
        SuppressDepth.Value++;
        return new SuppressScope();
    }

    internal static string? Detail(IReadOnlyDictionary<string, object?>? fields)
    {
        if (fields is null)
        {
            return null;
        }

        var parts = DetailFields
            .Where(k => fields.TryGetValue(k, out var v) && v is not null)
            .Select(k => k + "=" + Convert.ToString(fields[k], CultureInfo.InvariantCulture))
            .ToList();
        if (parts.Count == 0)
        {
            return null;
        }

        var detail = string.Join(", ", parts);
        return detail.Length <= MaxDetailLength ? detail : detail[..MaxDetailLength];
    }

    private sealed class SuppressScope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                SuppressDepth.Value--;
            }
        }
    }
}
