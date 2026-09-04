using System.Security.Cryptography;
using Jellyfin.Plugin.EasyNotif.Configuration;
using Jellyfin.Plugin.EasyNotif.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.EasyNotif.Inject;

/// <summary>
/// Hosted service that runs once at server startup: it makes sure the unsubscribe signing secret
/// exists, then registers the single index.html web transformation via the FileTransformation
/// plugin. Registered explicitly with <c>AddHostedService</c> in
/// <see cref="PluginServiceRegistrator"/> (an <see cref="IHostedService"/> does not show up under
/// Dashboard &gt; Scheduled Tasks, unlike an <c>IScheduledTask</c>).
/// </summary>
public sealed class StartupService : IHostedService
{
    private readonly ILogger<StartupService> _logger;
    private readonly IFileTransformationDetector _detector;
    private readonly IConfigAccessor _config;
    private readonly IEasyNotifLog _easyNotifLog;

    /// <summary>
    /// Initializes a new instance of the <see cref="StartupService"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    /// <param name="detector">FileTransformation reflection bridge.</param>
    /// <param name="config">Plugin configuration accessor.</param>
    /// <param name="easyNotifLog">The plugin's dedicated log.</param>
    public StartupService(
        ILogger<StartupService> logger,
        IFileTransformationDetector detector,
        IConfigAccessor config,
        IEasyNotifLog easyNotifLog)
    {
        _logger = logger;
        _detector = detector;
        _config = config;
        _easyNotifLog = easyNotifLog;
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
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _easyNotifLog.Info("plugin.shutdown");
        return Task.CompletedTask;
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
