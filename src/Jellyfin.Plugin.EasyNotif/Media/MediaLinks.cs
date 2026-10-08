using Jellyfin.Plugin.EasyNotif.Email;
using Jellyfin.Plugin.EasyNotif.Util;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.EasyNotif.Media;

/// <summary>
/// Builds the poster and detail links of an item in an email. With a valid public server URL, the
/// poster is the server's image endpoint and the title links to the item's page; without one, the
/// poster is an inline <c>cid:</c> attachment and the title is not linked.
/// </summary>
public static class MediaLinks
{
    /// <summary>Returns the public base URL without a trailing slash, or null when it is not valid.</summary>
    /// <param name="configuredUrl">The configured public server URL.</param>
    /// <returns>The base URL, or null.</returns>
    public static string? BaseUrl(string? configuredUrl)
        => PublicUrl.IsValid(configuredUrl) ? configuredUrl.TrimEnd('/') : null;

    /// <summary>Reads the inline posters of the items, only when there is no public base URL.</summary>
    /// <param name="baseUrl">The public base URL, or null.</param>
    /// <param name="posterCache">The inline poster cache.</param>
    /// <param name="items">The items.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The attachments by item id; empty with a public base URL.</returns>
    public static async Task<Dictionary<Guid, EmailAttachment>> InlinePostersAsync(
        string? baseUrl,
        IPosterCache posterCache,
        IEnumerable<BaseItem> items,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(posterCache);
        var inlinePosters = new Dictionary<Guid, EmailAttachment>();
        if (baseUrl is not null)
        {
            return inlinePosters;
        }

        foreach (var item in items)
        {
            var attachment = await posterCache.GetInlineAsync(item, cancellationToken).ConfigureAwait(false);
            if (attachment is not null)
            {
                inlinePosters[item.Id] = attachment;
            }
        }

        return inlinePosters;
    }

    /// <summary>Returns the poster link of an item.</summary>
    /// <param name="baseUrl">The public base URL, or null.</param>
    /// <param name="inlinePosters">The inline posters read when there is no public base URL.</param>
    /// <param name="item">The item.</param>
    /// <returns>The image URL, a <c>cid:</c> reference, or null.</returns>
    public static string? PosterUrl(string? baseUrl, IReadOnlyDictionary<Guid, EmailAttachment> inlinePosters, BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(inlinePosters);
        ArgumentNullException.ThrowIfNull(item);
        if (baseUrl is not null)
        {
            return item.HasImage(ImageType.Primary)
                ? $"{baseUrl}/Items/{item.Id:N}/Images/Primary?maxWidth=300&quality=85"
                : null;
        }

        return inlinePosters.TryGetValue(item.Id, out var attachment) ? $"cid:{attachment.ContentId}" : null;
    }

    /// <summary>Returns the web client page of an item.</summary>
    /// <param name="baseUrl">The public base URL, or null.</param>
    /// <param name="systemId">The server id for the deep link.</param>
    /// <param name="id">The item id.</param>
    /// <returns>The page URL, or null without a public base URL.</returns>
    public static string? DetailUrl(string? baseUrl, string systemId, Guid id)
        => baseUrl is null ? null : $"{baseUrl}/web/#/details?id={id:N}&serverId={systemId}";
}
