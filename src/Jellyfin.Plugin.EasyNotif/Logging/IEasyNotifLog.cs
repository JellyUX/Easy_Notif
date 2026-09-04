namespace Jellyfin.Plugin.EasyNotif.Logging;

/// <summary>
/// The plugin's own rolling log file (Synthese.md section 8), separate from Jellyfin's main log.
/// Every WARN/ERROR is also relayed to the standard <c>ILogger</c> so failures are never hidden
/// there. Email-shaped field values are masked at every level except <see cref="Debug"/>. Never
/// throws: a failure to open the file degrades to a no-op file sink while the relay keeps working.
/// </summary>
public interface IEasyNotifLog : IDisposable
{
    /// <summary>Writes a DEBUG entry. Field values are written unmasked.</summary>
    /// <param name="eventType">A short, stable event identifier (for example <c>"quota.tick"</c>).</param>
    /// <param name="fields">Structured fields for the entry, or null.</param>
    void Debug(string eventType, IReadOnlyDictionary<string, object?>? fields = null);

    /// <summary>Writes an INFO entry. Email-shaped field values are masked.</summary>
    /// <param name="eventType">A short, stable event identifier.</param>
    /// <param name="fields">Structured fields for the entry, or null.</param>
    void Info(string eventType, IReadOnlyDictionary<string, object?>? fields = null);

    /// <summary>Writes a WARN entry and relays it to the standard logger. Email-shaped field values are masked.</summary>
    /// <param name="eventType">A short, stable event identifier.</param>
    /// <param name="fields">Structured fields for the entry, or null.</param>
    /// <param name="exception">An associated exception, or null.</param>
    void Warn(string eventType, IReadOnlyDictionary<string, object?>? fields = null, Exception? exception = null);

    /// <summary>Writes an ERROR entry and relays it to the standard logger. Email-shaped field values are masked.</summary>
    /// <param name="eventType">A short, stable event identifier.</param>
    /// <param name="fields">Structured fields for the entry, or null.</param>
    /// <param name="exception">An associated exception, or null.</param>
    void Error(string eventType, IReadOnlyDictionary<string, object?>? fields = null, Exception? exception = null);

    /// <summary>Returns the last lines of the current log file, oldest first.</summary>
    /// <param name="count">How many lines to return at most.</param>
    /// <returns>The lines, or an empty list when no log file exists yet.</returns>
    IReadOnlyList<string> Tail(int count);
}
