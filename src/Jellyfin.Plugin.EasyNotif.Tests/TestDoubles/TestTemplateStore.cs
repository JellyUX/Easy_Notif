using Jellyfin.Plugin.EasyNotif.IO;
using Jellyfin.Plugin.EasyNotif.Storage;
using MediaBrowser.Common.Configuration;
using Moq;

namespace Jellyfin.Plugin.EasyNotif.Tests.TestDoubles;

/// <summary>
/// Builds a real <see cref="TemplateStore"/> for tests: embedded base templates work as-is, and
/// custom templates land under a throwaway temp directory.
/// </summary>
public static class TestTemplateStore
{
    /// <summary>Creates a store rooted at a fresh temp directory (or the given path).</summary>
    /// <param name="dataPath">The data path to use; a new temp path when null.</param>
    /// <returns>The store.</returns>
    public static TemplateStore Create(string? dataPath = null)
    {
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.DataPath)
            .Returns(dataPath ?? Path.Combine(Path.GetTempPath(), "enotif-tpl-tests-" + Guid.NewGuid()));
        return new TemplateStore(paths.Object, new FileSystem());
    }
}
