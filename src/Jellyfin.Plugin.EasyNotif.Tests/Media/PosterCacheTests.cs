using Jellyfin.Plugin.EasyNotif.IO;
using Jellyfin.Plugin.EasyNotif.Media;
using Jellyfin.Plugin.EasyNotif.Tests.TestDoubles;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Media;

/// <summary>
/// Covers <see cref="PosterCache"/>: an item with no image yields null, a small primary image is
/// attached with a stable content id, an oversized image is skipped with a warning, and a missing
/// file degrades to null without throwing.
/// </summary>
public sealed class PosterCacheTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "enotif-postercache-" + Guid.NewGuid());
    private readonly FakeEasyNotifLog _log = new();

    private PosterCache Build() => new(new FileSystem(), _log);

    private Movie MovieWithImage(string? onDiskPath)
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Film" };
        if (onDiskPath is not null)
        {
            movie.ImageInfos = [new ItemImageInfo { Type = ImageType.Primary, Path = onDiskPath }];
        }

        return movie;
    }

    private string WriteImage(int bytes)
    {
        Directory.CreateDirectory(_tempDir);
        var path = Path.Combine(_tempDir, "poster-" + Guid.NewGuid() + ".jpg");
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public async Task NoImage_ReturnsNull()
        => Assert.Null(await Build().GetInlineAsync(MovieWithImage(null), CancellationToken.None));

    [Fact]
    public async Task SmallImage_IsAttachedInline_WithAStableContentId()
    {
        var path = WriteImage(12);
        var movie = MovieWithImage(path);

        var result = await Build().GetInlineAsync(movie, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal($"poster-{movie.Id:N}", result!.ContentId);
        Assert.Equal("image/jpeg", result.ContentType);
        Assert.Equal(12, result.Content.Length);
    }

    [Fact]
    public async Task OversizedImage_IsSkipped_WithAWarning()
    {
        var path = WriteImage(PosterCache.MaxPosterBytes + 1);

        var result = await Build().GetInlineAsync(MovieWithImage(path), CancellationToken.None);

        Assert.Null(result);
        Assert.Contains(_log.Entries, e => e.EventType == "newsletter.poster.toolarge");
    }

    [Fact]
    public async Task MissingFile_ReturnsNull_WithoutThrowing()
        => Assert.Null(await Build().GetInlineAsync(MovieWithImage("/does/not/exist.jpg"), CancellationToken.None));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
