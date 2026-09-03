using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests;

/// <summary>
/// Locks the plugin identity. The GUID and name must stay identical across build.yaml,
/// manifest.json, Plugin.cs, deploy-plugin.ps1 and the FileTransformation registration payload.
/// </summary>
public class PluginMetadataTests
{
    private static Plugin BuildPlugin()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(p => p.PluginsPath).Returns(Path.GetTempPath());
        return new Plugin(paths.Object, Mock.Of<IXmlSerializer>());
    }

    [Fact]
    public void PluginName_IsEasyNotif()
    {
        Assert.Equal("Easy Notif", BuildPlugin().Name);
    }

    [Fact]
    public void PluginId_MatchesTheProjectGuid()
    {
        Assert.Equal(Guid.Parse("7a27339e-e774-4969-9755-8cd213dcc5e7"), BuildPlugin().Id);
    }
}
