using Jellyfin.Plugin.EasyNotif.Email;
using Jellyfin.Plugin.EasyNotif.IO;
using Jellyfin.Plugin.EasyNotif.Logging;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.EasyNotif.Media;

/// <summary>
/// Builds an inline <c>cid:</c> attachment from an item's primary image, for the emails sent when no
/// public server URL is configured (Synthese.md section 5.1). Read-only with respect to the library
/// (R12): the image is downscaled by Jellyfin's image processor into its own cache (DEC-55). Only a
/// JPEG, PNG or WebP file under Jellyfin's metadata directory, its image cache or a library folder is
/// read (DEC-52, DEC-53), and never one larger than <see cref="PosterCache.MaxPosterBytes"/>; any
/// other image is skipped and the item simply has no image.
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

    /// <summary>The width, in pixels, posters are downscaled to.</summary>
    public const int TargetWidth = 300;

    private const int TargetQuality = 82;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly IFileSystem _fileSystem;
    private readonly IServerApplicationPaths _applicationPaths;
    private readonly IImageProcessor _imageProcessor;
    private readonly ILibraryManager _libraryManager;
    private readonly IEasyNotifLog _log;

    /// <summary>Initializes a new instance of the <see cref="PosterCache"/> class.</summary>
    /// <param name="fileSystem">File system abstraction.</param>
    /// <param name="applicationPaths">Provides Jellyfin's metadata and image cache directories.</param>
    /// <param name="imageProcessor">Jellyfin's image processor, used to downscale the poster.</param>
    /// <param name="libraryManager">Provides the library folders.</param>
    /// <param name="log">The plugin's dedicated log.</param>
    public PosterCache(IFileSystem fileSystem, IServerApplicationPaths applicationPaths, IImageProcessor imageProcessor, ILibraryManager libraryManager, IEasyNotifLog log)
    {
        _fileSystem = fileSystem;
        _applicationPaths = applicationPaths;
        _imageProcessor = imageProcessor;
        _libraryManager = libraryManager;
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

            path = Path.GetFullPath(path);
            if (!IsUnderAllowedRoot(path))
            {
                Refused(item, "outside-roots");
                return null;
            }

            path = await DownscaleAsync(item, path).ConfigureAwait(false);
            if (!IsUnderAllowedRoot(path))
            {
                Refused(item, "outside-roots");
                return null;
            }

            var length = new FileInfo(path).Length;
            if (length > MaxPosterBytes)
            {
                TooLarge(item, length);
                return null;
            }

            var bytes = await ReadAtMostAsync(path, MaxPosterBytes + 1, cancellationToken).ConfigureAwait(false);
            if (bytes.Length > MaxPosterBytes)
            {
                TooLarge(item, bytes.Length);
                return null;
            }

            var format = DetectFormat(bytes);
            if (format is null)
            {
                Refused(item, "not-image");
                return null;
            }

            return new EmailAttachment
            {
                FileName = $"poster-{item.Id:N}{format.Value.Extension}",
                Content = bytes,
                ContentType = format.Value.ContentType,
                ContentId = $"poster-{item.Id:N}"
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            _log.Warn("newsletter.poster.skip", new Dictionary<string, object?>
            {
                ["itemId"] = item.Id,
                ["error"] = ex.Message
            });
            return null;
        }
    }

    /// <summary>Identifies a JPEG, PNG or WebP image by its leading bytes.</summary>
    /// <param name="bytes">The file content.</param>
    /// <returns>The content type and file extension, or null for anything else.</returns>
    internal static (string ContentType, string Extension)? DetectFormat(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return ("image/jpeg", ".jpg");
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return ("image/png", ".png");
        }

        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
        {
            return ("image/webp", ".webp");
        }

        return null;
    }

    /// <summary>
    /// Asks Jellyfin's image processor for a JPEG at most <see cref="TargetWidth"/> pixels wide (DEC-55).
    /// The processor caches the result under its own image cache. Any failure falls back to the
    /// original file, which then goes through the same size and format checks.
    /// </summary>
    private async Task<string> DownscaleAsync(BaseItem item, string originalPath)
    {
        try
        {
            var result = await _imageProcessor.ProcessImage(new ImageProcessingOptions
            {
                Item = item,
                ItemId = item.Id,
                Image = item.GetImageInfo(ImageType.Primary, 0),
                MaxWidth = TargetWidth,
                Quality = TargetQuality,
                SupportedOutputFormats = [ImageFormat.Jpg]
            }).ConfigureAwait(false);

            return string.IsNullOrEmpty(result.Path) ? originalPath : Path.GetFullPath(result.Path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn("newsletter.poster.resizefail", new Dictionary<string, object?>
            {
                ["itemId"] = item.Id,
                ["error"] = ex.Message
            });
            return originalPath;
        }
    }

    private static async Task<byte[]> ReadAtMostAsync(string path, int limit, CancellationToken cancellationToken)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using (stream.ConfigureAwait(false))
        {
            var buffer = new byte[limit];
            var total = 0;
            int read;
            while (total < limit && (read = await stream.ReadAsync(buffer.AsMemory(total, limit - total), cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
            }

            return buffer[..total];
        }
    }

    private static bool IsUnder(string path, string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var full = Path.GetFullPath(root);
        if (!Path.EndsInDirectorySeparator(full))
        {
            full += Path.DirectorySeparatorChar;
        }

        return path.StartsWith(full, PathComparison);
    }

    private bool IsUnderAllowedRoot(string path)
        => IsUnder(path, _applicationPaths.InternalMetadataPath)
            || IsUnder(path, _applicationPaths.ImageCachePath)
            || _libraryManager.GetVirtualFolders().Any(folder => folder.Locations?.Any(location => IsUnder(path, location)) == true);

    private void TooLarge(BaseItem item, long bytes)
        => _log.Warn("newsletter.poster.toolarge", new Dictionary<string, object?>
        {
            ["itemId"] = item.Id,
            ["bytes"] = bytes
        });

    private void Refused(BaseItem item, string reason)
        => _log.Warn("newsletter.poster.refused", new Dictionary<string, object?>
        {
            ["itemId"] = item.Id,
            ["reason"] = reason
        });
}
