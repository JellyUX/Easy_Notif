using System.Globalization;
using System.Net.Mail;
using System.Text;
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
    private const int TailBlockBytes = 64 * 1024;
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
            return ReadLastLines(stream, count);
        }
        catch (IOException)
        {
            return [];
        }
    }

    /// <summary>
    /// Returns the last lines of a UTF-8 stream, reading it backwards block by block so only the tail
    /// is loaded (PERF-03). Counting <c>\n</c> bytes is safe in UTF-8: that byte never occurs inside a
    /// multi-byte character.
    /// </summary>
    /// <param name="stream">A readable, seekable stream.</param>
    /// <param name="count">The number of lines wanted.</param>
    /// <returns>Up to <paramref name="count"/> lines, oldest first, without line terminators.</returns>
    internal static IReadOnlyList<string> ReadLastLines(Stream stream, int count)
    {
        var length = stream.Length;
        if (count <= 0 || length == 0)
        {
            return [];
        }

        // A terminator on the very last byte ends the last line rather than starting an empty one.
        var end = length;
        stream.Seek(-1, SeekOrigin.End);
        if (stream.ReadByte() == '\n')
        {
            end--;
        }

        var start = 0L;
        var found = 0;
        var buffer = new byte[TailBlockBytes];
        var position = end;
        while (position > 0 && start == 0)
        {
            var size = (int)Math.Min(TailBlockBytes, position);
            position -= size;
            stream.Seek(position, SeekOrigin.Begin);
            stream.ReadExactly(buffer, 0, size);
            for (var i = size - 1; i >= 0; i--)
            {
                if (buffer[i] == '\n' && ++found == count)
                {
                    start = position + i + 1;
                    break;
                }
            }
        }

        var tail = new byte[end - start];
        stream.Seek(start, SeekOrigin.Begin);
        stream.ReadExactly(tail);
        var text = Encoding.UTF8.GetString(tail);
        if (start == 0)
        {
            text = text.TrimStart('﻿');
        }

        return text.Length == 0 && end == 0
            ? []
            : text.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
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
