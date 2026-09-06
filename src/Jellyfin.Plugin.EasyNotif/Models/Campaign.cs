using System.Text.Json.Serialization;
using Jellyfin.Plugin.EasyNotif.Scheduling;

namespace Jellyfin.Plugin.EasyNotif.Models;

/// <summary>
/// The kind of a scheduled campaign. v1 has exactly two, both system-defined; there is no free CRUD
/// of campaigns (Synthese.md section 0).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CampaignType
{
    /// <summary>The newsletter of newly added media.</summary>
    Newsletter,

    /// <summary>The personalised weekly watch recap.</summary>
    WeeklyRecap
}

/// <summary>
/// A scheduled email campaign. The two rows (<c>newsletter</c>, <c>weekly-recap</c>) are seeded by
/// <see cref="Storage.CampaignStore"/> and only their <see cref="Enabled"/> flag, <see cref="Schedule"/>
/// and <see cref="MailLanguage"/> are editable by the admin.
/// </summary>
public sealed class Campaign
{
    /// <summary>Gets or sets the stable id: <c>newsletter</c> or <c>weekly-recap</c>.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the campaign kind.</summary>
    public CampaignType Type { get; set; }

    /// <summary>Gets or sets the opt-in category recipients are resolved from.</summary>
    public EmailCategory Category { get; set; }

    /// <summary>Gets or sets the recurrence rule.</summary>
    public RecurrenceSchedule Schedule { get; set; } = RecurrenceSchedule.Daily(new TimeOnly(9, 0));

    /// <summary>Gets or sets the template id used to compose the mail body (used from Phase 8).</summary>
    public string TemplateId { get; set; } = string.Empty;

    /// <summary>Gets or sets the language the mail is composed in (<c>fr</c> or <c>en</c>).</summary>
    public string MailLanguage { get; set; } = "fr";

    /// <summary>Gets or sets a value indicating whether the campaign is active.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the UTC time the campaign last dispatched, or null if never.</summary>
    public DateTime? LastSentUtc { get; set; }

    /// <summary>Gets or sets the next UTC time the campaign is due, or null when not scheduled yet.</summary>
    public DateTime? NextRunUtc { get; set; }
}

/// <summary>On-disk shape of <c>campaigns.json</c>.</summary>
public sealed class CampaignFile
{
    /// <summary>Gets or sets the storage schema version.</summary>
    public int Schema { get; set; } = 1;

    /// <summary>Gets the campaigns.</summary>
    public List<Campaign> Campaigns { get; init; } = [];
}
