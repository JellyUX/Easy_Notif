using System.Globalization;
using System.Net.Mail;
using Jellyfin.Plugin.EasyNotif.Util;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace Jellyfin.Plugin.EasyNotif.Logging;

/// <inheritdoc cref="IEasyNotifLog"/>
public sealed class EasyNotifLog : IEasyNotifLog
{
    private const long MaxFileSizeBytes = 10L * 1024 * 1024;
    private const string OutputTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

    private readonly string _logsDir;
    private readonly ILogger<EasyNotifLog> _relay;
    private readonly Serilog.Core.Logger? _logger;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="EasyNotifLog"/> class.</summary>
    /// <param name="applicationPaths">Provides the application data directory path.</param>
    /// <param name="configManager">Provides the server's activity log retention setting.</param>
    /// <param name="relay">The standard Jellyfin logger WARN/ERROR entries are also sent to.</param>
    public EasyNotifLog(IApplicationPaths applicationPaths, IServerConfigurationManager configManager, ILogger<EasyNotifLog> relay)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        ArgumentNullException.ThrowIfNull(configManager);
        _relay = relay;
        _logsDir = Path.Combine(applicationPaths.DataPath, "Jellyfin.Plugin.EasyNotif", "logs");

        try
        {
            Directory.CreateDirectory(_logsDir);
            var retentionDays = ClampRetentionDays(configManager.Configuration.ActivityLogRetentionDays ?? 30);
            _logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.Async(a => a.File(
                    Path.Combine(_logsDir, "easynotif-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: retentionDays,
                    fileSizeLimitBytes: MaxFileSizeBytes,
                    rollOnFileSizeLimit: true,
                    shared: false,
                    outputTemplate: OutputTemplate))
                .CreateLogger();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _relay.LogError(ex, "[EasyNotif] Could not open the dedicated log file; continuing without it.");
            _logger = null;
        }
    }

    /// <inheritdoc/>
    public void Debug(string eventType, IReadOnlyDictionary<string, object?>? fields = null)
        => Write(LogEventLevel.Debug, eventType, fields, exception: null);

    /// <inheritdoc/>
    public void Info(string eventType, IReadOnlyDictionary<string, object?>? fields = null)
        => Write(LogEventLevel.Information, eventType, fields, exception: null);

    /// <inheritdoc/>
    public void Warn(string eventType, IReadOnlyDictionary<string, object?>? fields = null, Exception? exception = null)
        => Write(LogEventLevel.Warning, eventType, fields, exception);

    /// <inheritdoc/>
    public void Error(string eventType, IReadOnlyDictionary<string, object?>? fields = null, Exception? exception = null)
        => Write(LogEventLevel.Error, eventType, fields, exception);

    /// <inheritdoc/>
    public IReadOnlyList<string> Tail(int count)
    {
        try
        {
            var file = LatestLogFile();
            if (file is null)
            {
                return [];
            }

            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var lines = new List<string>();
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                lines.Add(line);
            }

            return lines.Count <= count ? lines : lines.GetRange(lines.Count - count, count);
        }
        catch (IOException)
        {
            return [];
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _logger?.Dispose();
        _disposed = true;
    }

    /// <summary>Clamps a configured retention to at least one day.</summary>
    /// <param name="days">The configured <c>ActivityLogRetentionDays</c> value.</param>
    /// <returns>A value of at least 1.</returns>
    internal static int ClampRetentionDays(int days) => Math.Max(1, days);

    private void Write(LogEventLevel level, string eventType, IReadOnlyDictionary<string, object?>? fields, Exception? exception)
    {
        if (_logger is not null)
        {
            var effectiveFields = level == LogEventLevel.Debug ? fields : Mask(fields);
            _logger.Write(level, exception, "{EventType} {Fields}", eventType, FormatFields(effectiveFields));
        }

        if (level >= LogEventLevel.Warning)
        {
            _relay.Log(ToMsLevel(level), exception, "[EasyNotif] {EventType}", eventType);
        }
    }

    private string? LatestLogFile()
    {
        if (!Directory.Exists(_logsDir))
        {
            return null;
        }

        return Directory.GetFiles(_logsDir, "easynotif-*.log")
            .OrderByDescending(f => f, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static IReadOnlyDictionary<string, object?>? Mask(IReadOnlyDictionary<string, object?>? fields)
    {
        if (fields is null)
        {
            return null;
        }

        Dictionary<string, object?>? masked = null;
        foreach (var pair in fields)
        {
            if (pair.Value is string s && MailAddress.TryCreate(s, out _))
            {
                masked ??= new Dictionary<string, object?>(fields);
                masked[pair.Key] = EmailMasker.Mask(s);
            }
        }

        return masked ?? fields;
    }

    private static string FormatFields(IReadOnlyDictionary<string, object?>? fields)
        => fields is null || fields.Count == 0
            ? string.Empty
            : string.Join(' ', fields.Select(kv => $"{kv.Key}={FormatValue(kv.Value)}"));

    private static string FormatValue(object? value) => value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        string s when s.Contains(' ', StringComparison.Ordinal) => $"\"{s}\"",
        string s => s,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null"
    };

    private static LogLevel ToMsLevel(LogEventLevel level) => level switch
    {
        LogEventLevel.Warning => LogLevel.Warning,
        LogEventLevel.Error => LogLevel.Error,
        LogEventLevel.Fatal => LogLevel.Critical,
        _ => LogLevel.Information
    };
}
