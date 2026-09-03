using Jellyfin.Plugin.EasyNotif.Models;

namespace Jellyfin.Plugin.EasyNotif.Storage;

/// <summary>
/// Persistence contract for user email preferences. See <see cref="PreferencesStore"/>.
/// </summary>
public interface IPreferencesStore
{
    /// <summary>Returns a snapshot of every stored user preference.</summary>
    /// <returns>All stored preferences.</returns>
    IReadOnlyList<UserPreference> ReadAll();

    /// <summary>
    /// Applies a mutation to the preference list under a write lock. The mutation receives the live
    /// list and returns true if it changed anything (only then is the file rewritten).
    /// </summary>
    /// <param name="mutation">The mutation to apply; returns true when it modified the list.</param>
    void Mutate(Func<List<UserPreference>, bool> mutation);
}
