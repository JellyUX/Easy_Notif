using Jellyfin.Plugin.EasyNotif.Inject;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Inject;

/// <summary>
/// Covers the pure splice in <see cref="TransformationPatches.Inject"/>: both tags land before the
/// closing anchors, the script carries <c>data-series-bell="false"</c> when the bell is off, and
/// content with no anchors is returned untouched (R10).
/// </summary>
public class TransformationPatchesTests
{
    [Fact]
    public void Inject_SplicesBothTags_WhenAnchorsPresent()
    {
        const string Html = "<html><head></head><body><div></div></body></html>";

        var result = TransformationPatches.Inject(Html, "?v=1.2.3");

        Assert.Contains("/EasyNotif/enotif-user.css?v=1.2.3", result, StringComparison.Ordinal);
        Assert.Contains("/EasyNotif/enotif-user.js?v=1.2.3", result, StringComparison.Ordinal);
        Assert.Contains("defer></script>", result, StringComparison.Ordinal);
        Assert.True(
            result.IndexOf("enotif-user.css", StringComparison.Ordinal)
            < result.IndexOf("</head>", StringComparison.Ordinal));
    }

    [Fact]
    public void Inject_FlagsTheScript_WhenTheSeriesBellIsOff()
    {
        const string Html = "<html><head></head><body></body></html>";

        Assert.Contains(
            "<script src=\"/EasyNotif/enotif-user.js?v=1\" data-series-bell=\"false\" defer></script>",
            TransformationPatches.Inject(Html, "?v=1", seriesBell: false),
            StringComparison.Ordinal);
        Assert.DoesNotContain("data-series-bell", TransformationPatches.Inject(Html, "?v=1"), StringComparison.Ordinal);
    }

    [Fact]
    public void Inject_ReturnsInputUnchanged_WhenNoAnchors()
    {
        const string Html = "not really html";

        Assert.Equal(Html, TransformationPatches.Inject(Html, "?v=1"));
    }
}
