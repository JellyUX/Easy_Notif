using Jellyfin.Plugin.EasyNotif.Email;
using Jellyfin.Plugin.EasyNotif.IO;
using Jellyfin.Plugin.EasyNotif.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.EasyNotif.Media;

/// <summary>
/// Reads an item's primary image from disk as an inline <c>cid:</c> attachment, for the newsletter
/// when no public server URL is configured (Synthese.md section 5.1). Read-only with respect to the
/// library (R12). A poster larger than <see cref="MaxPosterBytes"/> is skipped rather than bloating
/// the message; the item then simply has no image.
/// </summary>
public interface IPosterCache
{
    /// <summary>Returns an inline poster attachment for an item, or null when there is no usable image.</summary>
    /// <param name="item">The movie or series.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The attachment, or null.</returns>
    Task<EmailAttachment?> GetInlineAsync(BaseItem item, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPosterCache"/>
public sealed class PosterCache : IPosterCache
{
    /// <summary>The largest primary image, in bytes, that is worth attaching inline.</summary>
    public const int MaxPosterBytes = 500 * 1024;

    private readonly IFileSystem _fileSystem;
    private readonly IEasyNotifLog _log;

    /// <summary>Initializes a new instance of the <see cref="PosterCache"/> class.</summary>
    /// <param name="fileSystem">File system abstraction.</param>
    /// <param name="log">The plugin's dedicated log.</param>
    public PosterCache(IFileSystem fileSystem, IEasyNotifLog log)
    {
        _fileSystem = fileSystem;
        _log = log;
    }

    /// <inheritdoc/>
    public async Task<EmailAttachment?> GetInlineAsync(BaseItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        try
        {
            if (!item.HasImage(ImageType.Primary))
            {
                return null;
            }

            var path = item.GetImagePath(ImageType.Primary, 0);
            if (string.IsNullOrEmpty(path) || !_fileSystem.FileExists(path))
            {
                return null;
            }

            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (bytes.Length > MaxPosterBytes)
            {
                _log.Warn("newsletter.poster.toolarge", new Dictionary<string, object?>
                {
                    ["itemId"] = item.Id,
                    ["bytes"] = bytes.Length
                });
                return null;
            }

            return new EmailAttachment
            {
                FileName = $"poster-{item.Id:N}{Path.GetExtension(path)}",
                Content = bytes,
                ContentType = ContentType(path),
                ContentId = $"poster-{item.Id:N}"
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            _log.Warn("newsletter.poster.skip", new Dictionary<string, object?>
            {
                ["itemId"] = item.Id,
                ["error"] = ex.Message
            });
            return null;
        }
    }

    private static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => "image/jpeg",
    };
}
