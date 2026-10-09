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

    [Theory]
    [InlineData("newsletter__..\\x")]
    [InlineData("newsletter__a/../b")]
    [InlineData("newsletter__../../secrets")]
    [InlineData("foo__bar")]
    [InlineData("__bar")]
    [InlineData("newsletter__Bad")]
    [InlineData("newsletter__-lead")]
    [InlineData("series-episodes__x")]
    [InlineData("newsletter__x\n")]
    public void MalformedCustomId_IsRefusedEverywhere_WithoutTouchingTheDisk(string id)
    {
        var store = Build();
        Directory.CreateDirectory(_tempDir);
        var outside = Path.Combine(_tempDir, "secrets-en.html");
        File.WriteAllText(outside, "outside");

        Assert.False(store.Exists(id));
        Assert.Throws<InvalidOperationException>(() => store.Get(id, "en"));
        Assert.Throws<InvalidOperationException>(() => store.GetRaw(id, "en"));
        Assert.Throws<InvalidOperationException>(() => store.Save(id, "en", "<p>x</p>"));
        Assert.Throws<InvalidOperationException>(() => store.Delete(id));
        Assert.True(File.Exists(outside));
        Assert.False(Directory.Exists(CustomDir));
    }

    [Fact]
    public void Clone_WithATrailingNewlineInTheSlug_IsRefused()
        => Assert.Throws<InvalidOperationException>(() => Build().Clone("newsletter", "x\n"));

    [Fact]
    public void List_IgnoresAFileWhoseIdDoesNotMatchTheCustomPattern()
    {
        Directory.CreateDirectory(CustomDir);
        File.WriteAllText(Path.Combine(CustomDir, "foo__bar-en.html"), "<p>x</p>");
        File.WriteAllText(Path.Combine(CustomDir, "series-episodes__x-en.html"), "<p>x</p>");

        Assert.DoesNotContain(Build().List(), t => t.Custom);
    }

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
    public void Validate_RejectsExcessivelyNestedContent()
    {
        const int depth = 40;
        var deep = string.Concat(Enumerable.Repeat("{{#if heading}}", depth))
            + "x"
            + string.Concat(Enumerable.Repeat("{{/if}}", depth));

        var result = Build().Validate("newsletter", deep);

        Assert.False(result.Ok);
        Assert.Equal("too-deep", result.Reason);
    }

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
    public void List_ReturnsTheFourBases_InOrder_PlusCustoms()
    {
        var store = Build();

        var bases = store.List().Where(t => !t.Custom).Select(t => t.Id).ToList();
        Assert.Equal(["newsletter", "weekly-recap", "series-episodes", "generic"], bases);
    }

    [Fact]
    public void SeriesEpisodes_IsAReadOnlyBase_ThatCannotBeCloned()
    {
        var store = Build();

        Assert.True(store.Exists("series-episodes"));
        Assert.Throws<InvalidOperationException>(() => store.Clone("series-episodes", "mine"));
        Assert.Throws<InvalidOperationException>(() => store.Save("series-episodes", "en", "<p>x</p>"));
        Assert.False(Directory.Exists(CustomDir) && Directory.EnumerateFiles(CustomDir).Any());
    }

    [Fact]
    public void Validate_SeriesEpisodes_KnowsItsOwnKeysOnly()
    {
        var store = Build();

        Assert.True(store.Validate("series-episodes", "{{#each series}}{{title}}{{#each episodes}}{{.}}{{/each}}<a href=\"{{stopUrl}}\">x</a>{{/each}}").Ok);
        var foreign = store.Validate("series-episodes", "<p>{{moreMoviesLabel}}</p>");
        Assert.False(foreign.Ok);
        Assert.Equal("moreMoviesLabel", foreign.Key);
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    public void Generic_IsAReadOnlyBase_ThatValidatesInBothLanguages(string lang)
    {
        var store = Build();

        Assert.True(store.Exists("generic"));
        Assert.True(store.Validate("generic", store.Get("generic", lang)).Ok);
        Assert.Throws<InvalidOperationException>(() => store.Clone("generic", "mine"));
        Assert.Throws<InvalidOperationException>(() => store.Save("generic", lang, "<p>x</p>"));
        Assert.Equal("unknown-key", store.Validate("generic", "<p>{{title}}</p>").Reason);
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
