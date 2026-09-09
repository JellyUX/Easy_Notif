using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.EasyNotif.IO;
using Jellyfin.Plugin.EasyNotif.Models;
using Jellyfin.Plugin.EasyNotif.Templating;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.EasyNotif.Storage;

/// <summary>
/// Provides the HTML body templates for campaign emails. The two base templates (<c>newsletter</c>,
/// <c>weekly-recap</c>) are embedded and read-only; the admin can clone one to an on-disk copy under
/// <c>{DataPath}/Jellyfin.Plugin.EasyNotif/templates/</c> and point a campaign at it
/// (Synthese.md section 3.5). A custom id is <c>{baseId}__{slug}</c>.
/// </summary>
public interface ITemplateStore
{
    /// <summary>Returns the effective template text for an id and a language.</summary>
    /// <param name="templateId">A base id (<c>newsletter</c> / <c>weekly-recap</c>) or a custom id.</param>
    /// <param name="lang">The language, <c>en</c> or <c>fr</c>.</param>
    /// <returns>The template text. A missing custom falls back to its <c>en</c> copy, then the base.</returns>
    /// <exception cref="InvalidOperationException">No template matches the id.</exception>
    string Get(string templateId, string lang);

    /// <summary>Returns the exact source of a template for the editor (embedded for a base, disk for a custom).</summary>
    /// <param name="templateId">The template id.</param>
    /// <param name="lang">The language.</param>
    /// <returns>The source text.</returns>
    string GetRaw(string templateId, string lang);

    /// <summary>Lists the base templates and every custom template on disk.</summary>
    /// <returns>The templates.</returns>
    IReadOnlyList<TemplateInfo> List();

    /// <summary>Clones a base template to a new on-disk custom template (both languages).</summary>
    /// <param name="baseId">The base id to clone.</param>
    /// <param name="slug">The new slug (<c>[a-z0-9][a-z0-9-]{0,30}</c>).</param>
    /// <exception cref="InvalidOperationException">The base id or slug is invalid, or the id exists.</exception>
    void Clone(string baseId, string slug);

    /// <summary>Validates a candidate template body against the known placeholders of its base id.</summary>
    /// <param name="baseId">The base id the body must be compatible with.</param>
    /// <param name="content">The candidate body.</param>
    /// <returns>The validation result.</returns>
    TemplateValidation Validate(string baseId, string content);

    /// <summary>Writes one language of a custom template to disk.</summary>
    /// <param name="templateId">A custom id.</param>
    /// <param name="lang">The language.</param>
    /// <param name="content">The body.</param>
    /// <exception cref="InvalidOperationException">The id is a read-only base template.</exception>
    void Save(string templateId, string lang, string content);

    /// <summary>Deletes a custom template (both languages).</summary>
    /// <param name="templateId">A custom id.</param>
    /// <exception cref="InvalidOperationException">The id is a read-only base template.</exception>
    void Delete(string templateId);

    /// <summary>Determines whether a template id resolves to something renderable.</summary>
    /// <param name="templateId">The template id.</param>
    /// <returns>True for a known base id or an existing custom id.</returns>
    bool Exists(string templateId);

    /// <summary>Returns the base id a template id belongs to.</summary>
    /// <param name="templateId">The template id.</param>
    /// <returns>The base id.</returns>
    string BaseIdOf(string templateId);
}

/// <summary>A template listed by <see cref="ITemplateStore.List"/>.</summary>
/// <param name="Id">The template id.</param>
/// <param name="BaseId">The base id it derives from (equal to <paramref name="Id"/> for a base).</param>
/// <param name="Custom">True for an admin-created on-disk template.</param>
/// <param name="Langs">The languages present (<c>en</c>, <c>fr</c>).</param>
public sealed record TemplateInfo(string Id, string BaseId, bool Custom, IReadOnlyList<string> Langs);

/// <summary>The result of validating a candidate template body.</summary>
/// <param name="Ok">True when the body is acceptable.</param>
/// <param name="Reason">A short machine reason when not: <c>too-large</c>, <c>script</c>, <c>too-deep</c>, <c>unknown-key</c>.</param>
/// <param name="Key">The offending placeholder for <c>unknown-key</c>.</param>
public sealed record TemplateValidation(bool Ok, string? Reason = null, string? Key = null);

/// <inheritdoc cref="ITemplateStore"/>
public sealed class TemplateStore : ITemplateStore
{
    /// <summary>The base template ids, in listing order.</summary>
    public static readonly IReadOnlyList<string> BaseIds = ["newsletter", "weekly-recap"];

    private const int MaxBytes = 64 * 1024;
    private const int MaxNesting = 32;
    private const string CustomSeparator = "__";

    private static readonly Regex SlugPattern = new("^[a-z0-9][a-z0-9-]{0,30}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Assembly PluginAssembly = typeof(TemplateStore).Assembly;

    private static readonly IReadOnlySet<string> NewsletterKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "heading", "summary", "isEmpty", "hasMovies", "hasSeries", "movies", "series",
        "title", "year", "genres", "overview", "posterUrl", "detailUrl", "noLink",
        "episodesLabel", "seasonsLabel", "unsubscribeUrl"
    };

    private static readonly IReadOnlySet<string> RecapKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "greeting", "heading", "quietWeek", "hasWatched", "watched",
        "yearTotal", "yearCompleted", "partialSince",
        "title", "seriesTitle", "isSeries", "isSingle", "episodeCount", "when", "completed", "unsubscribeUrl"
    };

    private readonly ConcurrentDictionary<string, string> _embeddedCache = new(StringComparer.Ordinal);
    private readonly IFileSystem _fileSystem;
    private readonly string _customDir;

    /// <summary>Initializes a new instance of the <see cref="TemplateStore"/> class.</summary>
    /// <param name="applicationPaths">Provides the application data directory path.</param>
    /// <param name="fileSystem">File system abstraction.</param>
    public TemplateStore(IApplicationPaths applicationPaths, IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        _fileSystem = fileSystem;
        _customDir = Path.Combine(applicationPaths.DataPath, "Jellyfin.Plugin.EasyNotif", "templates");
    }

    /// <summary>Returns the base id used to compose a campaign's mail when it has no explicit template.</summary>
    /// <param name="type">The campaign kind.</param>
    /// <returns>The base id.</returns>
    public static string DefaultTemplateId(CampaignType type)
        => type == CampaignType.WeeklyRecap ? "weekly-recap" : "newsletter";

    /// <inheritdoc/>
    public string BaseIdOf(string templateId)
    {
        var sep = templateId.IndexOf(CustomSeparator, StringComparison.Ordinal);
        return sep > 0 ? templateId[..sep] : templateId;
    }

    /// <inheritdoc/>
    public bool Exists(string templateId)
    {
        if (BaseIds.Contains(templateId))
        {
            return true;
        }

        return IsCustomId(templateId) && _fileSystem.FileExists(CustomPath(templateId, "en"));
    }

    /// <inheritdoc/>
    public string Get(string templateId, string lang)
    {
        var normalizedLang = NormalizeLang(lang);

        if (BaseIds.Contains(templateId))
        {
            return LoadEmbedded(templateId, normalizedLang);
        }

        if (!IsCustomId(templateId))
        {
            throw new InvalidOperationException($"Unknown template \"{templateId}\".");
        }

        var baseId = BaseIdOf(templateId);
        if (!BaseIds.Contains(baseId))
        {
            throw new InvalidOperationException($"Unknown base template for \"{templateId}\".");
        }

        var path = CustomPath(templateId, normalizedLang);
        if (_fileSystem.FileExists(path))
        {
            return _fileSystem.ReadAllText(path);
        }

        var englishPath = CustomPath(templateId, "en");
        if (normalizedLang != "en" && _fileSystem.FileExists(englishPath))
        {
            return _fileSystem.ReadAllText(englishPath);
        }

        return LoadEmbedded(baseId, normalizedLang);
    }

    /// <inheritdoc/>
    public string GetRaw(string templateId, string lang)
    {
        var normalizedLang = NormalizeLang(lang);

        if (BaseIds.Contains(templateId))
        {
            return LoadEmbedded(templateId, normalizedLang);
        }

        if (!IsCustomId(templateId))
        {
            throw new InvalidOperationException($"Unknown template \"{templateId}\".");
        }

        var path = CustomPath(templateId, normalizedLang);
        if (_fileSystem.FileExists(path))
        {
            return _fileSystem.ReadAllText(path);
        }

        var englishPath = CustomPath(templateId, "en");
        if (_fileSystem.FileExists(englishPath))
        {
            return _fileSystem.ReadAllText(englishPath);
        }

        throw new InvalidOperationException($"No source for template \"{templateId}\" ({normalizedLang}).");
    }

    /// <inheritdoc/>
    public IReadOnlyList<TemplateInfo> List()
    {
        var result = new List<TemplateInfo>(BaseIds.Select(id => new TemplateInfo(id, id, false, ["en", "fr"])));

        var byId = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var file in _fileSystem.EnumerateFiles(_customDir, "*.html"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var dash = name.LastIndexOf('-');
            if (dash <= 0)
            {
                continue;
            }

            var id = name[..dash];
            var fileLang = name[(dash + 1)..];
            if ((fileLang != "en" && fileLang != "fr") || !IsCustomId(id))
            {
                continue;
            }

            if (!byId.TryGetValue(id, out var langs))
            {
                langs = new SortedSet<string>(StringComparer.Ordinal);
                byId[id] = langs;
            }

            langs.Add(fileLang);
        }

        foreach (var (id, langs) in byId.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            result.Add(new TemplateInfo(id, BaseIdOf(id), true, [.. langs]));
        }

        return result;
    }

    /// <inheritdoc/>
    public void Clone(string baseId, string slug)
    {
        if (!BaseIds.Contains(baseId))
        {
            throw new InvalidOperationException($"Unknown base template \"{baseId}\".");
        }

        if (string.IsNullOrEmpty(slug) || !SlugPattern.IsMatch(slug))
        {
            throw new InvalidOperationException("Invalid slug.");
        }

        var customId = baseId + CustomSeparator + slug;
        if (Exists(customId))
        {
            throw new InvalidOperationException($"Template \"{customId}\" already exists.");
        }

        _fileSystem.CreateDirectory(_customDir);
        WriteAtomic(CustomPath(customId, "en"), LoadEmbedded(baseId, "en"));
        WriteAtomic(CustomPath(customId, "fr"), LoadEmbedded(baseId, "fr"));
    }

    /// <inheritdoc/>
    public TemplateValidation Validate(string baseId, string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (System.Text.Encoding.UTF8.GetByteCount(content) > MaxBytes)
        {
            return new TemplateValidation(false, "too-large");
        }

        if (content.Contains("<script", StringComparison.OrdinalIgnoreCase))
        {
            return new TemplateValidation(false, "script");
        }

        if (TemplateEngine.NestingDepth(content) > MaxNesting)
        {
            return new TemplateValidation(false, "too-deep");
        }

        var allowed = KnownKeys(baseId);
        foreach (var key in TemplateEngine.ReferencedKeys(content))
        {
            if (!allowed.Contains(key))
            {
                return new TemplateValidation(false, "unknown-key", key);
            }
        }

        return new TemplateValidation(true);
    }

    /// <inheritdoc/>
    public void Save(string templateId, string lang, string content)
    {
        if (BaseIds.Contains(templateId))
        {
            throw new InvalidOperationException("Base templates are read-only.");
        }

        if (!IsCustomId(templateId) || !BaseIds.Contains(BaseIdOf(templateId)))
        {
            throw new InvalidOperationException($"Unknown template \"{templateId}\".");
        }

        _fileSystem.CreateDirectory(_customDir);
        WriteAtomic(CustomPath(templateId, NormalizeLang(lang)), content);
    }

    /// <inheritdoc/>
    public void Delete(string templateId)
    {
        if (BaseIds.Contains(templateId))
        {
            throw new InvalidOperationException("Base templates are read-only.");
        }

        foreach (var lang in new[] { "en", "fr" })
        {
            var path = CustomPath(templateId, lang);
            if (_fileSystem.FileExists(path))
            {
                _fileSystem.Delete(path);
            }
        }
    }

    private static bool IsCustomId(string templateId)
        => templateId.Contains(CustomSeparator, StringComparison.Ordinal);

    private static string NormalizeLang(string lang) => lang is "fr" ? "fr" : "en";

    private static IReadOnlySet<string> KnownKeys(string baseId)
        => baseId == "weekly-recap" ? RecapKeys : NewsletterKeys;

    private string CustomPath(string templateId, string lang)
        => Path.Combine(_customDir, $"{templateId}-{lang}.html");

    private void WriteAtomic(string path, string content)
    {
        var temp = path + ".tmp";
        _fileSystem.WriteAllText(temp, content);
        _fileSystem.Move(temp, path, overwrite: true);
    }

    private string LoadEmbedded(string templateId, string lang)
        => _embeddedCache.GetOrAdd($"{templateId}.{lang}", _ => LoadEmbeddedCore(templateId, lang));

    private static string LoadEmbeddedCore(string templateId, string lang)
    {
        var suffix = string.Create(CultureInfo.InvariantCulture, $".templates.{templateId}-{lang}.html");
        var name = PluginAssembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

        if (name is null && lang != "en")
        {
            return LoadEmbeddedCore(templateId, "en");
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
