using Jellyfin.Plugin.EasyNotif.IO;
using Jellyfin.Plugin.EasyNotif.Models;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EasyNotif.Storage;

/// <summary>
/// The single source of persistence for user email preferences: reads and writes
/// <c>{DataPath}/Jellyfin.Plugin.EasyNotif/preferences.json</c>. Locking, atomic writes, the
/// in-memory cache and corrupt-file recovery come from <see cref="JsonFileStore{T}"/>.
/// </summary>
public sealed class PreferencesStore : JsonFileStore<PreferencesFile>, IPreferencesStore
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PreferencesStore"/> class.
    /// </summary>
    /// <param name="applicationPaths">Provides the application data directory path.</param>
    /// <param name="fileSystem">File system abstraction, for testability.</param>
    /// <param name="logger">Logger.</param>
    public PreferencesStore(IApplicationPaths applicationPaths, IFileSystem fileSystem, ILogger<PreferencesStore> logger)
        : base(applicationPaths, fileSystem, logger, "preferences.json")
    {
    }

    /// <inheritdoc/>
    public IReadOnlyList<UserPreference> ReadAll()
        => Read(file => (IReadOnlyList<UserPreference>)[.. file.Users]);

    /// <inheritdoc/>
    public void Mutate(Func<List<UserPreference>, bool> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        base.Mutate(file => mutation(file.Users));
    }
}
