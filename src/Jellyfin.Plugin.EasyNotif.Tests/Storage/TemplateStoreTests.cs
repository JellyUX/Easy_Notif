using Jellyfin.Plugin.EasyNotif.Storage;
using Xunit;

namespace Jellyfin.Plugin.EasyNotif.Tests.Storage;

/// <summary>
/// Covers <see cref="TemplateStore"/>: the two embedded newsletter templates load and differ,
/// an unknown language falls back to English, an unknown template id throws, and repeated reads
/// are cached.
/// </summary>
public sealed class TemplateStoreTests
{
    [Fact]
    public void LoadsBothNewsletterLanguages_AndTheyDiffer()
    {
        var store = new TemplateStore();

        var en = store.Get("newsletter", "en");
        var fr = store.Get("newsletter", "fr");

        Assert.False(string.IsNullOrWhiteSpace(en));
        Assert.False(string.IsNullOrWhiteSpace(fr));
        Assert.NotEqual(en, fr);
        Assert.Contains("Movies", en, StringComparison.Ordinal);
        Assert.Contains("Films", fr, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownLanguage_FallsBackToEnglish()
    {
        var store = new TemplateStore();

        Assert.Equal(store.Get("newsletter", "en"), store.Get("newsletter", "de"));
    }

    [Fact]
    public void UnknownTemplateId_Throws()
        => Assert.Throws<InvalidOperationException>(() => new TemplateStore().Get("does-not-exist", "en"));

    [Fact]
    public void RepeatedReads_ReturnTheSameCachedString()
    {
        var store = new TemplateStore();

        Assert.Same(store.Get("newsletter", "fr"), store.Get("newsletter", "fr"));
    }
}
