using System.Text.Json;
using Jellyfin.Plugin.EasyNotif.IO;
using Jellyfin.Plugin.EasyNotif.Storage;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EasyNotif.Email;

/// <summary>
/// Appends one line per send to <c>send-log.jsonl</c> and updates a line's status when a Resend
/// webhook arrives, keyed by the Resend message id. Recipient addresses are stored masked only
/// (R15 / RGPD, Synthese.md section 11). Capped at the most recent 500 entries.
/// </summary>
public interface ISendLog
{
    /// <summary>Adds one send entry.</summary>
    /// <param name="entry">The entry. Its <see cref="SendLogEntry.ToMasked"/> must already be masked.</param>
    void Append(SendLogEntry entry);

    /// <summary>Sets the status of every entry with the given Resend id.</summary>
    /// <param name="resendId">The Resend message id.</param>
    /// <param name="status">The new status (a webhook event type, for example <c>email.delivered</c>).</param>
    /// <returns>True when at least one entry matched.</returns>
    bool UpdateStatus(string resendId, string status);

    /// <summary>Returns the most recent entries, newest first.</summary>
    /// <param name="count">How many to return.</param>
    /// <returns>The entries.</returns>
    IReadOnlyList<SendLogEntry> Recent(int count);

    /// <summary>Returns the time and type of the last webhook-driven status update, if any.</summary>
    /// <returns>The UTC time and status, or nulls.</returns>
    (DateTime? Utc, string? Type) LastWebhook();
}

/// <summary>One recorded send.</summary>
public sealed record SendLogEntry
{
    /// <summary>Gets the UTC time the send was attempted.</summary>
    public DateTime Ts { get; init; }

    /// <summary>Gets the send context: <c>test</c>, <c>manual</c>, <c>newsletter</c> or <c>recap</c>.</summary>
    public string Context { get; init; } = string.Empty;

    /// <summary>Gets the masked recipient address.</summary>
    public string ToMasked { get; init; } = string.Empty;

    /// <summary>Gets the category, for a category-bound send; null otherwise.</summary>
    public string? Category { get; init; }

    /// <summary>Gets the subject line.</summary>
    public string Subject { get; init; } = string.Empty;

    /// <summary>Gets the Resend message id, or null when the send never left.</summary>
    public string? ResendId { get; init; }

    /// <summary>Gets the current status: <c>sent</c>, <c>failed</c>, or a webhook event type.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Gets the UTC time a webhook last updated this entry's status, if ever.</summary>
    public DateTime? UpdatedAt { get; init; }
}

/// <summary>On-disk shape of <c>send-log.jsonl</c> (one JSON object per line).</summary>
public sealed class SendLogFile
{
    /// <summary>Gets the recorded entries, oldest first.</summary>
    public List<SendLogEntry> Entries { get; } = [];
}

/// <inheritdoc cref="ISendLog"/>
public sealed class SendLog : JsonFileStore<SendLogFile>, ISendLog
{
    private const int MaxEntries = 500;
    private static readonly JsonSerializerOptions LineOptions = new();

    private readonly Func<DateTimeOffset> _now;

    /// <summary>Initializes a new instance of the <see cref="SendLog"/> class.</summary>
    /// <param name="applicationPaths">Provides the application data directory path.</param>
    /// <param name="fileSystem">File system abstraction.</param>
    /// <param name="logger">Logger.</param>
    public SendLog(IApplicationPaths applicationPaths, IFileSystem fileSystem, ILogger<SendLog> logger)
        : this(applicationPaths, fileSystem, logger, () => DateTimeOffset.UtcNow)
    {
    }

    internal SendLog(IApplicationPaths applicationPaths, IFileSystem fileSystem, ILogger<SendLog> logger, Func<DateTimeOffset> now)
        : base(applicationPaths, fileSystem, logger, "send-log.jsonl")
        => _now = now;

    /// <inheritdoc/>
    public void Append(SendLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Mutate(file =>
        {
            file.Entries.Add(entry);
            if (file.Entries.Count > MaxEntries)
            {
                file.Entries.RemoveRange(0, file.Entries.Count - MaxEntries);
            }

            return true;
        });
    }

    /// <inheritdoc/>
    public bool UpdateStatus(string resendId, string status)
    {
        if (string.IsNullOrEmpty(resendId))
        {
            return false;
        }

        var updated = false;
        var now = _now().UtcDateTime;
        Mutate(file =>
        {
            for (var i = 0; i < file.Entries.Count; i++)
            {
                if (file.Entries[i].ResendId == resendId)
                {
                    file.Entries[i] = file.Entries[i] with { Status = status, UpdatedAt = now };
                    updated = true;
                }
            }

            return updated;
        });
        return updated;
    }

    /// <inheritdoc/>
    public IReadOnlyList<SendLogEntry> Recent(int count)
        => Read(file => (IReadOnlyList<SendLogEntry>)[.. Enumerable.Reverse(file.Entries).Take(Math.Max(0, count))]);

    /// <inheritdoc/>
    public (DateTime? Utc, string? Type) LastWebhook()
        => Read(file =>
        {
            var last = file.Entries
                .Where(e => e.UpdatedAt is not null)
                .OrderByDescending(e => e.UpdatedAt)
                .FirstOrDefault();
            return (last?.UpdatedAt, last?.Status);
        });

    /// <inheritdoc/>
    protected override string Serialize(SendLogFile value)
        => string.Join('\n', value.Entries.Select(e => JsonSerializer.Serialize(e, LineOptions)));

    /// <inheritdoc/>
    protected override SendLogFile Deserialize(string raw)
    {
        var file = new SendLogFile();
        foreach (var line in raw.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            try
            {
                var entry = JsonSerializer.Deserialize<SendLogEntry>(trimmed, LineOptions);
                if (entry is not null)
                {
                    file.Entries.Add(entry);
                }
            }
            catch (JsonException)
            {
                // A single unreadable line does not lose the rest of the log.
            }
        }

        return file;
    }
}
