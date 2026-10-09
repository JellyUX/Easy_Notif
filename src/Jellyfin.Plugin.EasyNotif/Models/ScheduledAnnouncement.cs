using System.Text.Json.Serialization;
using Jellyfin.Plugin.EasyNotif.Email;

namespace Jellyfin.Plugin.EasyNotif.Models;

/// <summary>
/// A manual email programmed for a date (FEAT-05). Sent once by the dispatch tick, then kept with
/// its counters for <see cref="Storage.ScheduledAnnouncementStore.FinishedRetention"/>. Recipients are
/// resolved at send time, so an opt-out of announcements made in the meantime is honoured (DEC-72).
/// No attachments (DEC-67).
/// </summary>
public sealed class ScheduledAnnouncement
{
    /// <summary>Waiting for its date.</summary>
    public const string Pending = "pending";

    /// <summary>Sent, with transient failures waiting for another attempt (DEC-71).</summary>
    public const string Retrying = "retrying";

    /// <summary>Done: every recipient was attempted, and any retry finished or was abandoned.</summary>
    public const string Sent = "sent";

    /// <summary>Cancelled by the admin before its date.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>Not sendable at its date (the request no longer validates).</summary>
    public const string Failed = "failed";

    /// <summary>Gets or sets the id, also the lot id of its idempotency keys (<c>manual:{id}:{user}</c>).</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the subject.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>Gets or sets the HTML body, or null.</summary>
    public string? Html { get; set; }

    /// <summary>Gets or sets the plain-text body, or null.</summary>
    public string? Text { get; set; }

    /// <summary>Gets or sets a value indicating whether the body is laid out with the <c>generic</c> template.</summary>
    public bool Wrap { get; set; }

    /// <summary>Gets or sets the layout language, <c>fr</c> or <c>en</c>.</summary>
    public string Lang { get; set; } = "fr";

    /// <summary>Gets or sets the audience: <c>all</c> or <c>selected</c>.</summary>
    public string Mode { get; set; } = "all";

    /// <summary>Gets or sets the chosen users when <see cref="Mode"/> is <c>selected</c>.</summary>
    public List<Guid> UserIds { get; set; } = [];

    /// <summary>Gets or sets the UTC send time.</summary>
    public DateTime SendAtUtc { get; set; }

    /// <summary>Gets or sets the UTC time the announcement was programmed.</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>Gets or sets the state: <see cref="Pending"/>, <see cref="Retrying"/>, <see cref="Sent"/>, <see cref="Cancelled"/> or <see cref="Failed"/>.</summary>
    public string Status { get; set; } = Pending;

    /// <summary>Gets or sets the number of messages the provider accepted.</summary>
    public int SentCount { get; set; }

    /// <summary>Gets or sets the number of recipients that did not get the message for good.</summary>
    public int FailedCount { get; set; }

    /// <summary>Gets or sets the number of intended users without a contact address at send time.</summary>
    public int SkippedNoEmail { get; set; }

    /// <summary>Gets or sets the number of send passes made.</summary>
    public int Attempts { get; set; }

    /// <summary>Gets or sets the next retry time while <see cref="Status"/> is <see cref="Retrying"/>.</summary>
    public DateTime? NextAttemptUtc { get; set; }

    /// <summary>Gets or sets the recipients whose send failed transiently and are retried.</summary>
    public List<Guid> RetryUserIds { get; set; } = [];

    /// <summary>Gets or sets the UTC time the announcement reached a final state.</summary>
    public DateTime? FinishedUtc { get; set; }

    /// <summary>Gets a value indicating whether the announcement is final (nothing more will be sent).</summary>
    [JsonIgnore]
    public bool IsFinished => Status is Sent or Cancelled or Failed;

    /// <summary>Returns the manual email request this announcement sends.</summary>
    /// <returns>The request.</returns>
    public ManualEmailRequest ToRequest()
        => new(Subject, Html, Text, Mode, Mode == "selected" ? UserIds : null, null, null) { Wrap = Wrap, Lang = Lang };

    /// <summary>Returns a detached copy.</summary>
    /// <returns>The copy.</returns>
    public ScheduledAnnouncement Clone()
    {
        var copy = (ScheduledAnnouncement)MemberwiseClone();
        copy.UserIds = [.. UserIds];
        copy.RetryUserIds = [.. RetryUserIds];
        return copy;
    }
}

/// <summary>On-disk shape of <c>scheduled-announcements.json</c>.</summary>
public sealed class ScheduledAnnouncementFile
{
    /// <summary>Gets or sets the storage schema version.</summary>
    public int Schema { get; set; } = 1;

    /// <summary>Gets the announcements.</summary>
    public List<ScheduledAnnouncement> Announcements { get; init; } = [];
}
