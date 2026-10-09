using Jellyfin.Plugin.EasyNotif.IO;
using Jellyfin.Plugin.EasyNotif.Media;
using Jellyfin.Plugin.EasyNotif.Tests.TestDoubles;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Media;

/// <summary>
/// Covers <see cref="PosterCache"/>: an item with no image yields null, a small JPEG, PNG or WebP
/// under the metadata directory or a library folder is attached with a stable content id and a type
/// taken from its bytes, and anything else (outside those roots, not an image, a GIF, oversized,
/// missing) is skipped without throwing (SEC-02, DEC-52, DEC-53).
/// </summary>
public sealed class PosterCacheTests : IDisposable
{
    private static readonly byte[] JpegHeader = [0xFF, 0xD8, 0xFF, 0xE0];
    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] WebpHeader = [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8];

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "enotif-postercache-" + Guid.NewGuid());
    private readonly string _metadataDir;
    private readonly string _libraryDir;
    private readonly string _outsideDir;
    private readonly FakeEasyNotifLog _log = new();
    private readonly Mock<ILibraryManager> _library = new();

    public PosterCacheTests()
    {
        _metadataDir = Path.Combine(_tempDir, "metadata");
        _libraryDir = Path.Combine(_tempDir, "media");
        _outsideDir = Path.Combine(_tempDir, "metadata-other");
        _library.Setup(l => l.GetVirtualFolders())
            .Returns([new VirtualFolderInfo { Name = "Films", Locations = [_libraryDir] }]);
    }

    private PosterCache Build()
    {
        var paths = new Mock<IServerApplicationPaths>();
        paths.SetupGet(p => p.InternalMetadataPath).Returns(_metadataDir);
        return new PosterCache(new FileSystem(), paths.Object, _library.Object, _log);
    }

    private static Movie MovieWithImage(string? onDiskPath)
    {
        var movie = new Movie { Id = Guid.NewGuid(), Name = "Film" };
        if (onDiskPath is not null)
        {
            movie.ImageInfos = [new ItemImageInfo { Type = ImageType.Primary, Path = onDiskPath }];
        }

        return movie;
    }

    private static string Write(string dir, string name, byte[] header, int totalBytes)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        var bytes = new byte[Math.Max(totalBytes, header.Length)];
        header.CopyTo(bytes, 0);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task NoImage_ReturnsNull()
        => Assert.Null(await Build().GetInlineAsync(MovieWithImage(null), CancellationToken.None));

    [Fact]
    public async Task SmallJpeg_UnderTheMetadataDirectory_IsAttachedInline_WithAStableContentId()
    {
        var path = Write(Path.Combine(_metadataDir, "library", "ab"), "poster.jpg", JpegHeader, 12);
        var movie = MovieWithImage(path);

        var result = await Build().GetInlineAsync(movie, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal($"poster-{movie.Id:N}", result!.ContentId);
        Assert.Equal("image/jpeg", result.ContentType);
        Assert.Equal($"poster-{movie.Id:N}.jpg", result.FileName);
        Assert.Equal(12, result.Content.Length);
    }

    [Theory]
    [InlineData("png", "image/png", ".png")]
    [InlineData("webp", "image/webp", ".webp")]
    public async Task PngAndWebp_TakeTheirTypeFromTheBytes_NotTheExtension(string format, string contentType, string extension)
    {
        var header = format == "png" ? PngHeader : WebpHeader;
        var path = Write(_metadataDir, "poster.jpg", header, 64);

        var result = await Build().GetInlineAsync(MovieWithImage(path), CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(contentType, result!.ContentType);
        Assert.EndsWith(extension, result.FileName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImageStoredNextToTheMedia_UnderALibraryFolder_IsAttached()
    {
        var path = Write(Path.Combine(_libraryDir, "Film (2020)"), "poster.jpg", JpegHeader, 32);

        Assert.NotNull(await Build().GetInlineAsync(MovieWithImage(path), CancellationToken.None));
    }

    [Fact]
    public async Task PathOutsideTheMetadataAndLibraryFolders_IsRefused()
    {
        var path = Write(_outsideDir, "secret.jpg", JpegHeader, 32);

        var result = await Build().GetInlineAsync(MovieWithImage(path), CancellationToken.None);

        Assert.Null(result);
        Assert.Contains(_log.Entries, e => e.EventType == "newsletter.poster.refused"
            && (string?)e.Fields!["reason"] == "outside-roots");
    }

    [Fact]
    public async Task PathEscapingTheMetadataDirectoryWithDotDot_IsRefused()
    {
        Write(_outsideDir, "secret.jpg", JpegHeader, 32);
        var escaping = Path.Combine(_metadataDir, "..", "metadata-other", "secret.jpg");

        Assert.Null(await Build().GetInlineAsync(MovieWithImage(escaping), CancellationToken.None));
        Assert.Contains(_log.Entries, e => e.EventType == "newsletter.poster.refused");
    }

    [Fact]
    public async Task TextFileRenamedToJpg_IsRefused()
    {
        var path = Write(_metadataDir, "poster.jpg", "root:x:0:0:root:/root:/bin/sh\n"u8.ToArray(), 0);

        var result = await Build().GetInlineAsync(MovieWithImage(path), CancellationToken.None);

        Assert.Null(result);
        Assert.Contains(_log.Entries, e => e.EventType == "newsletter.poster.refused"
            && (string?)e.Fields!["reason"] == "not-image");
    }

    [Fact]
    public async Task Gif_IsRefused()
    {
        var path = Write(_metadataDir, "poster.gif", "GIF89a"u8.ToArray(), 32);

        Assert.Null(await Build().GetInlineAsync(MovieWithImage(path), CancellationToken.None));
    }

    [Fact]
    public async Task OversizedImage_IsSkipped_WithAWarning()
    {
        var path = Write(_metadataDir, "poster.jpg", JpegHeader, PosterCache.MaxPosterBytes + 1);

        var result = await Build().GetInlineAsync(MovieWithImage(path), CancellationToken.None);

        Assert.Null(result);
        Assert.Contains(_log.Entries, e => e.EventType == "newsletter.poster.toolarge");
    }

    [Fact]
    public async Task FiftyMegabyteFile_IsRefusedFromItsLength_WithoutBeingRead()
    {
        Directory.CreateDirectory(_metadataDir);
        var path = Path.Combine(_metadataDir, "poster.jpg");
        const long size = 50L * 1024 * 1024;
        using (var stream = File.Create(path))
        {
            stream.SetLength(size);
        }

        var result = await Build().GetInlineAsync(MovieWithImage(path), CancellationToken.None);

        // The bounded read would report at most MaxPosterBytes + 1: the full size proves the
        // refusal came from FileInfo.Length, before the file was opened.
        Assert.Null(result);
        var entry = Assert.Single(_log.Entries);
        Assert.Equal("newsletter.poster.toolarge", entry.EventType);
        Assert.Equal(size, entry.Fields!["bytes"]);
    }

    [Fact]
    public async Task MissingFile_ReturnsNull_WithoutThrowing()
        => Assert.Null(await Build().GetInlineAsync(MovieWithImage("/does/not/exist.jpg"), CancellationToken.None));

    [Fact]
    public void DetectFormat_RecognisesOnlyJpegPngAndWebp()
    {
        Assert.Equal("image/jpeg", PosterCache.DetectFormat(JpegHeader)?.ContentType);
        Assert.Equal("image/png", PosterCache.DetectFormat(PngHeader)?.ContentType);
        Assert.Equal("image/webp", PosterCache.DetectFormat(WebpHeader)?.ContentType);
        Assert.Null(PosterCache.DetectFormat("GIF89a"u8));
        Assert.Null(PosterCache.DetectFormat("RIFF\0\0\0\0WAVE"u8));
        Assert.Null(PosterCache.DetectFormat([]));
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
