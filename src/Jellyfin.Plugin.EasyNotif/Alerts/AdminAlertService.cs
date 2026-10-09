using System.Globalization;
using Jellyfin.Plugin.EasyNotif.Configuration;
using Jellyfin.Plugin.EasyNotif.Email;
using Jellyfin.Plugin.EasyNotif.Logging;
using Jellyfin.Plugin.EasyNotif.Scheduling;

namespace Jellyfin.Plugin.EasyNotif.Alerts;

/// <summary>
/// Sends the incident digest to <see cref="PluginConfiguration.AlertEmail"/> from the dispatch tick
/// (FEAT-04, DEC-63): at the first tick that finds incidents newer than the last digest, at most once
/// per calendar day of the scheduler time zone. The day only counts as served once the transport
/// accepts the digest, so a digest that fails (an invalid key) goes out at the first tick after the
/// transport is repaired. See <see cref="AdminAlertService"/>.
/// </summary>
public interface IAdminAlertService
{
    /// <summary>Sends the digest when one is due.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True when a digest was accepted by the transport.</returns>
    Task<bool> RunAsync(CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAdminAlertService"/>
public sealed class AdminAlertService : IAdminAlertService
{
    private readonly IIncidentStore _incidents;
    private readonly IConfigAccessor _config;
    private readonly IEmailSender _sender;
    private readonly IEasyNotifLog _log;
    private readonly Func<DateTime> _now;

    /// <summary>Initializes a new instance of the <see cref="AdminAlertService"/> class.</summary>
    /// <param name="incidents">The incident store.</param>
    /// <param name="config">The plugin configuration accessor.</param>
    /// <param name="sender">The email transport.</param>
    /// <param name="log">The plugin log.</param>
    public AdminAlertService(IIncidentStore incidents, IConfigAccessor config, IEmailSender sender, IEasyNotifLog log)
        : this(incidents, config, sender, log, () => DateTime.UtcNow)
    {
    }

    internal AdminAlertService(IIncidentStore incidents, IConfigAccessor config, IEmailSender sender, IEasyNotifLog log, Func<DateTime> now)
    {
        _incidents = incidents;
        _config = config;
        _sender = sender;
        _log = log;
        _now = now;
    }

    /// <inheritdoc/>
    public async Task<bool> RunAsync(CancellationToken cancellationToken)
    {
        var cfg = _config.Get();
        if (string.IsNullOrWhiteSpace(cfg.AlertEmail))
        {
            return false;
        }

        var now = _now();
        var last = _incidents.LastDigestUtc;
        var tz = RecurrenceSchedule.ResolveTimeZone(cfg.SchedulerTimeZone);
        if (last is { } lastUtc && LocalDay(lastUtc, tz) == LocalDay(now, tz))
        {
            return false;
        }

        var pending = _incidents.All().Where(i => last is null || i.Ts > last).ToList();
        if (pending.Count == 0)
        {
            return false;
        }

        var (subject, html) = AdminAlertMail.Build(cfg.AlertLanguage, pending);
        var message = new EmailMessage
        {
            To = cfg.AlertEmail.Trim(),
            Subject = subject,
            Html = html,
            // One key per attempt: a key reused after a refused send could replay the refusal.
            IdempotencyKey = "alert:" + now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)
        };

        SendResult result;
        using (_incidents.Suppress())
        {
            result = await _sender.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }

        if (!result.Success)
        {
            // Not an incident type: the banner already shows what went wrong; retried next tick.
            _log.Warn("alert.failed", new Dictionary<string, object?> { ["count"] = pending.Count, ["error"] = result.Error });
            return false;
        }

        _incidents.MarkDigestSent(now);
        _log.Info("alert.sent", new Dictionary<string, object?> { ["count"] = pending.Count });
        return true;
    }

    private static DateOnly LocalDay(DateTime utc, TimeZoneInfo tz)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz));
}
