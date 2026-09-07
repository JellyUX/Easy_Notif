using Jellyfin.Plugin.EasyNotif.IO;
using Jellyfin.Plugin.EasyNotif.Media;
using Jellyfin.Plugin.EasyNotif.Tests.TestDoubles;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Media;

/// <summary>
/// Covers <see cref="PosterCache"/>: an item with no image yields null, the first call resizes and
/// caches while the second reads the cache, a processor failure with no readable original degrades
/// to null (never throws), and the cache is bounded by a file-count cap on write.
/// </summary>
public sealed class PosterCacheTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "enotif-postercache-" + Guid.NewGuid());
    private readonly Mock<IImageProcessor> _processor = new();
    private readonly FakeEasyNotifLog _log = new();

    private string CacheDir => Path.Combine(_tempDir, "Jellyfin.Plugin.EasyNotif", "imgcache");

    private PosterCache Build()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.DataPath).Returns(_tempDir);
        return new PosterCache(_processor.Object, paths.Object, new FileSystem(), _log);
    }

    private Movie MovieWithImage(string? onDiskPath)
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Film" };
        if (onDiskPath is not null)
        {
            movie.ImageInfos = [new ItemImageInfo { Type = ImageType.Primary, Path = onDiskPath }];
        }

        return movie;
    }

    private string WriteSourceImage(byte[] bytes)
    {
        Directory.CreateDirectory(_tempDir);
        var path = Path.Combine(_tempDir, "source-" + Guid.NewGuid() + ".jpg");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task NoImage_ReturnsNull()
    {
        var result = await Build().GetInlineAsync(MovieWithImage(null), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task FirstCall_ResizesAndCaches_SecondCallReadsTheCache()
    {
        var source = WriteSourceImage([1, 2, 3, 4]);
        _processor
            .Setup(p => p.ProcessImage(It.IsAny<ImageProcessingOptions>()))
            .ReturnsAsync((source, "image/jpeg", DateTime.UtcNow));
        var movie = MovieWithImage("/original.jpg");
        var cache = Build();

        var first = await cache.GetInlineAsync(movie, CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal($"poster-{movie.Id:N}", first!.ContentId);
        Assert.Equal([1, 2, 3, 4], first.Content);
        Assert.Single(Directory.EnumerateFiles(CacheDir, "*.jpg"));

        var second = await cache.GetInlineAsync(movie, CancellationToken.None);

        Assert.Equal([1, 2, 3, 4], second!.Content);
        _processor.Verify(p => p.ProcessImage(It.IsAny<ImageProcessingOptions>()), Times.Once);
    }

    [Fact]
    public async Task ProcessorFails_AndNoReadableOriginal_ReturnsNull_LogsAWarning()
    {
        _processor
            .Setup(p => p.ProcessImage(It.IsAny<ImageProcessingOptions>()))
            .ThrowsAsync(new InvalidOperationException("no encoder"));

        var result = await Build().GetInlineAsync(MovieWithImage("/does/not/exist.jpg"), CancellationToken.None);

        Assert.Null(result);
        Assert.Contains(_log.Entries, e => e.EventType == "newsletter.poster.skip");
    }

    [Fact]
    public async Task Cache_IsBoundedByAFileCountCap_OnWrite()
    {
        Directory.CreateDirectory(CacheDir);
        for (var i = 0; i < 44; i++)
        {
            File.WriteAllBytes(Path.Combine(CacheDir, $"old-{i}.jpg"), [0]);
        }

        var source = WriteSourceImage([9]);
        _processor
            .Setup(p => p.ProcessImage(It.IsAny<ImageProcessingOptions>()))
            .ReturnsAsync((source, "image/jpeg", DateTime.UtcNow));

        await Build().GetInlineAsync(MovieWithImage("/x.jpg"), CancellationToken.None);

        Assert.True(Directory.EnumerateFiles(CacheDir, "*.jpg").Count() <= 40);
    }

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
