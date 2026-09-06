using System.Collections.Concurrent;
using System.Reflection;

namespace Jellyfin.Plugin.EasyNotif.Storage;

/// <summary>
/// Provides the HTML body templates for campaign emails. Phase 8 serves the two embedded newsletter
/// templates (<c>en</c> / <c>fr</c>); a later phase adds admin-editable copies on disk. See
/// <see cref="TemplateStore"/>.
/// </summary>
public interface ITemplateStore
{
    /// <summary>Returns the template text for an id and a language, falling back to <c>en</c>.</summary>
    /// <param name="templateId">The template id, for example <c>newsletter</c>.</param>
    /// <param name="lang">The language, <c>en</c> or <c>fr</c>.</param>
    /// <returns>The template text.</returns>
    /// <exception cref="InvalidOperationException">No embedded template matches the id.</exception>
    string Get(string templateId, string lang);
}

/// <inheritdoc cref="ITemplateStore"/>
public sealed class TemplateStore : ITemplateStore
{
    private static readonly Assembly PluginAssembly = typeof(TemplateStore).Assembly;
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public string Get(string templateId, string lang)
    {
        var normalizedLang = lang is "fr" ? "fr" : "en";
        return _cache.GetOrAdd($"{templateId}.{normalizedLang}", key => Load(templateId, normalizedLang));
        // Phase 10: check {DataPath}/Jellyfin.Plugin.EasyNotif/templates/ for an admin override first.
    }

    private static string Load(string templateId, string lang)
    {
        var suffix = $".templates.{templateId}-{lang}.html";
        var name = PluginAssembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

        if (name is null && lang != "en")
        {
            return Load(templateId, "en");
        }

        if (name is null)
        {
            throw new InvalidOperationException($"No embedded template for \"{templateId}\".");
        }

        using var stream = PluginAssembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
