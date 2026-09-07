using Jellyfin.Plugin.EasyNotif.IO;
using Jellyfin.Plugin.EasyNotif.Storage;
using MediaBrowser.Common.Configuration;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Storage;

/// <summary>
/// Covers <see cref="TemplateStore"/>: the two base templates resolve from embedded resources and
/// are read-only, a clone lands on disk in both languages, a custom id reads the disk copy with an
/// <c>en</c> and then a base fallback, validation rejects a script tag / an unknown placeholder / an
/// oversized body, and delete removes both languages.
/// </summary>
public sealed class TemplateStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "enotif-templatestore-" + Guid.NewGuid());

    private string CustomDir => Path.Combine(_tempDir, "Jellyfin.Plugin.EasyNotif", "templates");

    private TemplateStore Build()
    {
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.DataPath).Returns(_tempDir);
        return new TemplateStore(paths.Object, new FileSystem());
    }

    [Fact]
    public void BaseTemplates_ResolveFromEmbeddedResources_AndDifferByLanguage()
    {
        var store = Build();

        var fr = store.Get("newsletter", "fr");
        var en = store.Get("newsletter", "en");

        Assert.Contains("Films", fr, StringComparison.Ordinal);
        Assert.Contains("Movies", en, StringComparison.Ordinal);
        Assert.Contains("This week", store.Get("weekly-recap", "en"), StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownLanguage_FallsBackToEnglish()
    {
        var store = Build();

        Assert.Equal(store.Get("newsletter", "en"), store.Get("newsletter", "de"));
    }

    [Fact]
    public void UnknownId_Throws()
        => Assert.Throws<InvalidOperationException>(() => Build().Get("nope", "en"));

    [Fact]
    public void Clone_WritesBothLanguages_AndListsTheCustom()
    {
        var store = Build();

        store.Clone("newsletter", "holiday");

        Assert.True(File.Exists(Path.Combine(CustomDir, "newsletter__holiday-en.html")));
        Assert.True(File.Exists(Path.Combine(CustomDir, "newsletter__holiday-fr.html")));

        var custom = Assert.Single(store.List(), t => t.Custom);
        Assert.Equal("newsletter__holiday", custom.Id);
        Assert.Equal("newsletter", custom.BaseId);
        Assert.Equal(["en", "fr"], custom.Langs);
    }

    [Fact]
    public void Clone_RejectsAnInvalidSlugOrDuplicate()
    {
        var store = Build();

        Assert.Throws<InvalidOperationException>(() => store.Clone("newsletter", "Bad Slug"));
        Assert.Throws<InvalidOperationException>(() => store.Clone("no-such-base", "x"));

        store.Clone("newsletter", "one");
        Assert.Throws<InvalidOperationException>(() => store.Clone("newsletter", "one"));
    }

    [Fact]
    public void Get_ForACustom_ReadsTheDiskCopy_ThenFallsBackToEnglish_ThenBase()
    {
        var store = Build();
        store.Clone("newsletter", "x");

        store.Save("newsletter__x", "fr", "<p>{{heading}} FR CUSTOM</p>");
        Assert.Contains("FR CUSTOM", store.Get("newsletter__x", "fr"), StringComparison.Ordinal);

        // Remove the fr copy: fr should fall back to the custom's en copy, not the base.
        File.Delete(Path.Combine(CustomDir, "newsletter__x-fr.html"));
        store.Save("newsletter__x", "en", "<p>{{heading}} EN CUSTOM</p>");
        Assert.Contains("EN CUSTOM", store.Get("newsletter__x", "fr"), StringComparison.Ordinal);

        // Remove the en copy too: now it falls back to the embedded base.
        File.Delete(Path.Combine(CustomDir, "newsletter__x-en.html"));
        Assert.Equal(store.Get("newsletter", "en"), store.Get("newsletter__x", "en"));
    }

    [Fact]
    public void Save_RejectsABaseTemplate()
        => Assert.Throws<InvalidOperationException>(() => Build().Save("newsletter", "en", "<p>x</p>"));

    [Fact]
    public void Validate_RejectsScript_UnknownKey_AndOversize_AcceptsAnIntactClone()
    {
        var store = Build();

        Assert.False(store.Validate("newsletter", "<script>alert(1)</script>").Ok);

        var unknown = store.Validate("newsletter", "<p>{{heading}} {{mystery}}</p>");
        Assert.False(unknown.Ok);
        Assert.Equal("unknown-key", unknown.Reason);
        Assert.Equal("mystery", unknown.Key);

        Assert.False(store.Validate("newsletter", new string('x', 70 * 1024)).Ok);

        Assert.True(store.Validate("newsletter", store.Get("newsletter", "en")).Ok);
        Assert.True(store.Validate("weekly-recap", store.Get("weekly-recap", "fr")).Ok);
    }

    [Fact]
    public void Validate_AllowsTheUnsubscribeUrlPlaceholder()
        => Assert.True(Build().Validate("newsletter", "<a href=\"{{unsubscribeUrl}}\">x</a>").Ok);

    [Fact]
    public void Delete_RemovesBothLanguages()
    {
        var store = Build();
        store.Clone("weekly-recap", "gone");

        store.Delete("weekly-recap__gone");

        Assert.False(File.Exists(Path.Combine(CustomDir, "weekly-recap__gone-en.html")));
        Assert.False(File.Exists(Path.Combine(CustomDir, "weekly-recap__gone-fr.html")));
        Assert.DoesNotContain(store.List(), t => t.Custom);
    }

    [Fact]
    public void List_ReturnsTheTwoBases_InOrder_PlusCustoms()
    {
        var store = Build();

        var bases = store.List().Where(t => !t.Custom).Select(t => t.Id).ToList();
        Assert.Equal(["newsletter", "weekly-recap"], bases);
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
