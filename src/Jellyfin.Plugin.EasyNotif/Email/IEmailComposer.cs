using Jellyfin.Plugin.EasyNotif.Models;
using Jellyfin.Plugin.EasyNotif.Services;

namespace Jellyfin.Plugin.EasyNotif.Email;

/// <summary>The rendered subject and body of a campaign email.</summary>
/// <param name="Subject">The subject line.</param>
/// <param name="Html">The HTML body, or null.</param>
/// <param name="Text">The plain-text body, or null (the sender derives one from the HTML when absent).</param>
public sealed record EmailContent(string Subject, string? Html, string? Text);

/// <summary>
/// The result of preparing a campaign for one run: whether it should actually be sent, and a
/// per-recipient render delegate. <see cref="Render"/> is always set for a found campaign (an empty
/// newsletter still renders a "nothing new" body, used by the admin preview).
/// </summary>
public sealed class PreparedCampaign
{
    /// <summary>Gets a value indicating whether the campaign has content worth sending.</summary>
    public required bool ShouldSend { get; init; }

    /// <summary>Gets why the campaign was not sent, when <see cref="ShouldSend"/> is false.</summary>
    public string? SkipReason { get; init; }

    /// <summary>Gets the render delegate producing the email for a recipient.</summary>
    public required Func<Recipient, EmailContent> Render { get; init; }

    /// <summary>Gets the movie count of the prepared digest (newsletter only; 0 otherwise).</summary>
    public int MovieCount { get; init; }

    /// <summary>Gets the series count of the prepared digest (newsletter only; 0 otherwise).</summary>
    public int SeriesCount { get; init; }
}

/// <summary>
/// Composes campaign emails once per run, routed by <see cref="Campaign.Type"/>. Replaces the
/// Phase 7 stub content builder (Synthese.md section 9.1).
/// </summary>
public interface IEmailComposer
{
    /// <summary>Prepares a campaign for a run at <paramref name="nowUtc"/>.</summary>
    /// <param name="campaign">The campaign.</param>
    /// <param name="nowUtc">The run instant (UTC); bounds the digest window.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="freshWindow">When true, the digest looks back a fixed seven days and ignores
    /// <see cref="Campaign.LastSentUtc"/>. Used by the admin preview so it always shows the last
    /// week regardless of when the campaign last ran.</param>
    /// <returns>The prepared campaign.</returns>
    Task<PreparedCampaign> PrepareAsync(Campaign campaign, DateTime nowUtc, CancellationToken cancellationToken, bool freshWindow = false);
}
