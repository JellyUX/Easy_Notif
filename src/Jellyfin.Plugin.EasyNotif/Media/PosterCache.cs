using System.Globalization;
using Jellyfin.Plugin.EasyNotif.Email;
using Jellyfin.Plugin.EasyNotif.IO;
using Jellyfin.Plugin.EasyNotif.Logging;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.EasyNotif.Media;

/// <summary>
/// Produces a downsized JPEG of an item's primary image as an inline <c>cid:</c> attachment, for the
/// newsletter when no public server URL is configured (Synthese.md section 5.1). Results are cached
/// under <c>{DataPath}/Jellyfin.Plugin.EasyNotif/imgcache/</c>, bounded by a size and file-count cap
/// on write. Read-only with respect to the library (R12): it only resizes an existing image.
/// </summary>
public interface IPosterCache
{
    /// <summary>Returns an inline poster attachment for an item, or null when there is no image or a read fails.</summary>
    /// <param name="item">The movie or series.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The attachment, or null.</returns>
    Task<EmailAttachment?> GetInlineAsync(BaseItem item, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IPosterCache"/>
public sealed class PosterCache : IPosterCache
{
    private const int MaxFiles = 40;
    private const long MaxBytes = 8L * 1024 * 1024;
    private const int TargetWidth = 300;

    private readonly IImageProcessor _imageProcessor;
    private readonly IFileSystem _fileSystem;
    private readonly IEasyNotifLog _log;
    private readonly string _dir;

    /// <summary>Initializes a new instance of the <see cref="PosterCache"/> class.</summary>
    /// <param name="imageProcessor">The Jellyfin image processor.</param>
    /// <param name="applicationPaths">Provides the data directory path.</param>
    /// <param name="fileSystem">File system abstraction.</param>
    /// <param name="log">The plugin's dedicated log.</param>
    public PosterCache(IImageProcessor imageProcessor, IApplicationPaths applicationPaths, IFileSystem fileSystem, IEasyNotifLog log)
    {
        _imageProcessor = imageProcessor;
        _fileSystem = fileSystem;
        _log = log;
        _dir = Path.Combine(applicationPaths.DataPath, "Jellyfin.Plugin.EasyNotif", "imgcache");
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

            var info = item.GetImageInfo(ImageType.Primary, 0);
            var tag = _imageProcessor.GetImageCacheTag(item, info)
                ?? info.DateModified.Ticks.ToString(CultureInfo.InvariantCulture);
            var path = Path.Combine(_dir, $"{item.Id:N}-{Sanitize(tag)}.jpg");

            byte[] bytes;
            if (_fileSystem.FileExists(path))
            {
                bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _fileSystem.CreateDirectory(_dir);
                bytes = await RenderAsync(item, info, cancellationToken).ConfigureAwait(false);
                await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
                Evict();
            }

            return new EmailAttachment
            {
                FileName = $"poster-{item.Id:N}.jpg",
                Content = bytes,
                ContentType = "image/jpeg",
                ContentId = $"poster-{item.Id:N}"
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            _log.Warn("newsletter.poster.skip", new Dictionary<string, object?>
            {
                ["itemId"] = item.Id,
                ["error"] = ex.Message
            });
            return null;
        }
    }

    private async Task<byte[]> RenderAsync(BaseItem item, ItemImageInfo info, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _imageProcessor.ProcessImage(new ImageProcessingOptions
            {
                Item = item,
                Image = info,
                MaxWidth = TargetWidth,
                Quality = 82
            }).ConfigureAwait(false);

            return await File.ReadAllBytesAsync(result.Path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warn("newsletter.poster.rawfallback", new Dictionary<string, object?>
            {
                ["itemId"] = item.Id,
                ["error"] = ex.Message
            });
            return await File.ReadAllBytesAsync(item.GetImagePath(ImageType.Primary, 0), cancellationToken).ConfigureAwait(false);
        }
    }

    private void Evict()
    {
        var files = _fileSystem.EnumerateFiles(_dir, "*.jpg")
            .Select(p => new FileInfo(p))
            .Where(f => f.Exists)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();

        long running = 0;
        for (var i = 0; i < files.Count; i++)
        {
            running += files[i].Length;
            if (i >= MaxFiles || running > MaxBytes)
            {
                try
                {
                    _fileSystem.Delete(files[i].FullName);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private static string Sanitize(string tag)
        => new(tag.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
}
