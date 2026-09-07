using System.Security.Cryptography;
using Jellyfin.Plugin.EasyNotif.Configuration;
using Jellyfin.Plugin.EasyNotif.Logging;
using Jellyfin.Plugin.EasyNotif.Media;
using Jellyfin.Plugin.EasyNotif.Models;
using Jellyfin.Plugin.EasyNotif.Playback;
using Jellyfin.Plugin.EasyNotif.Scheduling;
using Jellyfin.Plugin.EasyNotif.Storage;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.EasyNotif.Inject;

/// <summary>
/// Hosted service that runs once at server startup: it makes sure the unsubscribe signing secret
/// exists, then registers the single index.html web transformation via the FileTransformation
/// plugin. Registered explicitly with <c>AddHostedService</c> in
/// <see cref="PluginServiceRegistrator"/> (an <see cref="IHostedService"/> does not show up under
/// Dashboard &gt; Scheduled Tasks, unlike a scheduled task).
/// </summary>
public sealed class StartupService : IHostedService
{
    private readonly ILogger<StartupService> _logger;
    private readonly IFileTransformationDetector _detector;
    private readonly IConfigAccessor _config;
    private readonly IEasyNotifLog _easyNotifLog;
    private readonly ICampaignStore _campaigns;
    private readonly ILibraryManager _libraryManager;
    private readonly IAddedItemsStore _addedItems;
    private readonly ISessionManager _sessionManager;
    private readonly IPlaybackHistoryStore _history;

    /// <summary>
    /// Initializes a new instance of the <see cref="StartupService"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    /// <param name="detector">FileTransformation reflection bridge.</param>
    /// <param name="config">Plugin configuration accessor.</param>
    /// <param name="easyNotifLog">The plugin's dedicated log.</param>
    /// <param name="campaigns">The campaign store.</param>
    /// <param name="libraryManager">The Jellyfin library manager (for the ItemAdded event).</param>
    /// <param name="addedItems">The server-time added-items store.</param>
    /// <param name="sessionManager">The Jellyfin session manager (for the PlaybackStopped event).</param>
    /// <param name="history">The plugin's own playback history store.</param>
    public StartupService(
        ILogger<StartupService> logger,
        IFileTransformationDetector detector,
        IConfigAccessor config,
        IEasyNotifLog easyNotifLog,
        ICampaignStore campaigns,
        ILibraryManager libraryManager,
        IAddedItemsStore addedItems,
        ISessionManager sessionManager,
        IPlaybackHistoryStore history)
    {
        _logger = logger;
        _detector = detector;
        _config = config;
        _easyNotifLog = easyNotifLog;
        _campaigns = campaigns;
        _libraryManager = libraryManager;
        _addedItems = addedItems;
        _sessionManager = sessionManager;
        _history = history;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var cfg = _config.Get();
        if (EnsureUnsubscribeSecret(cfg))
        {
            _config.Save();
            _logger.LogInformation("[EasyNotif] Generated the unsubscribe signing secret.");
        }

        _easyNotifLog.Info("plugin.startup", new Dictionary<string, object?>
        {
            ["version"] = Plugin.Instance?.Version?.ToString() ?? "unknown",
            ["publicServerUrlSet"] = !string.IsNullOrWhiteSpace(cfg.PublicServerUrl),
            ["timeZone"] = cfg.SchedulerTimeZone
        });

        ScheduleEnabledCampaigns(cfg);

        _addedItems.Start();
        _libraryManager.ItemAdded += OnItemAdded;

        _history.Start();
        _sessionManager.PlaybackStopped += OnPlaybackStopped;

        if (!_detector.IsAvailable())
        {
            const string Warning =
                "FileTransformation plugin is not installed. Easy Notif cannot inject its settings "
                + "panel into the web client. Install jellyfin-plugin-file-transformation and restart "
                + "Jellyfin. The plugin's HTTP API still works.";

            _logger.LogError("[EasyNotif] {Warning}", Warning);
            _easyNotifLog.Error("filetransformation.missing", new Dictionary<string, object?> { ["present"] = false });
            SetStartupWarning(Warning);
            return Task.CompletedTask;
        }

        SetStartupWarning(null);
        RegisterIndexHtmlTransformation();
        _easyNotifLog.Info("filetransformation.detected", new Dictionary<string, object?> { ["present"] = true });
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        await _addedItems.StopAsync().ConfigureAwait(false);
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        await _history.StopAsync().ConfigureAwait(false);
        _easyNotifLog.Info("plugin.shutdown");
    }

    /// <summary>
    /// Records newly added movies and episodes in real server time, so the newsletter can find them
    /// regardless of the item's own <c>DateCreated</c> (which Jellyfin takes from the file system).
    /// Non-blocking: it only queues the id (R13).
    /// </summary>
    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is Movie or Episode)
        {
            _addedItems.RecordAdded(e.Item.Id);
        }
    }

    /// <summary>
    /// Records a significant playback stop (movie or episode, watched past three minutes) in the
    /// plugin's own history, for the personalised weekly recap. Non-blocking: it only queues the
    /// event (R13). Fires once per user in the session.
    /// </summary>
    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        if (e.Item is null)
        {
            return;
        }

        foreach (var user in e.Users)
        {
            if (PlaybackEventFactory.TryBuild(e.Item, e.PlaybackPositionTicks, e.PlayedToCompletion, user.Id, DateTime.UtcNow, out var playbackEvent))
            {
                _history.Record(playbackEvent);
            }
        }
    }

    /// <summary>
    /// Fills <see cref="PluginConfiguration.UnsubscribeSecret"/> with a fresh 32-byte base64url
    /// value when it is empty. Returns true when it generated one (the caller then persists).
    /// </summary>
    /// <param name="config">The configuration to fill.</param>
    /// <returns>True when a secret was generated.</returns>
    internal static bool EnsureUnsubscribeSecret(PluginConfiguration config)
    {
        if (!string.IsNullOrEmpty(config.UnsubscribeSecret))
        {
            return false;
        }

        var bytes = RandomNumberGenerator.GetBytes(32);
        config.UnsubscribeSecret = Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
        return true;
    }

    /// <summary>
    /// Fills a first <see cref="Campaign.NextRunUtc"/> for every enabled campaign that has none. A
    /// value already in the past (missed while the server was down) is left untouched: the next tick
    /// fires it once, then re-anchors from that run.
    /// </summary>
    /// <param name="cfg">The plugin configuration (for the scheduler time zone).</param>
    private void ScheduleEnabledCampaigns(PluginConfiguration cfg)
    {
        var tz = RecurrenceSchedule.ResolveTimeZone(cfg.SchedulerTimeZone);
        var now = DateTime.UtcNow;

        foreach (var campaign in _campaigns.All().Where(c => c.Enabled && c.NextRunUtc is null))
        {
            var next = campaign.Schedule.NextRunUtc(now, tz);
            _campaigns.Update(campaign.Id, c => c.NextRunUtc = next);
            _easyNotifLog.Info("campaign.scheduled", new Dictionary<string, object?>
            {
                ["campaignId"] = campaign.Id,
                ["nextRunUtc"] = next
            });
        }
    }

    private void RegisterIndexHtmlTransformation()
    {
        var payload = new JObject
        {
            ["id"] = Plugin.Instance?.Id.ToString() ?? Guid.NewGuid().ToString(),
            ["fileNamePattern"] = "index.html",
            ["callbackAssembly"] = typeof(TransformationPatches).Assembly.FullName,
            ["callbackClass"] = typeof(TransformationPatches).FullName,
            ["callbackMethod"] = nameof(TransformationPatches.IndexHtml)
        };

        _detector.RegisterTransformation(payload);
        _logger.LogInformation("[EasyNotif] Registered index.html transformation.");
    }

    private void SetStartupWarning(string? warning)
    {
        var cfg = _config.Get();
        if (cfg.StartupWarning == warning)
        {
            return;
        }

        cfg.StartupWarning = warning;
        _config.Save();
    }
}
