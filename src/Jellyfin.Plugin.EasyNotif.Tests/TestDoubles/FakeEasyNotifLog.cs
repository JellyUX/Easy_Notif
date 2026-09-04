using Jellyfin.Plugin.EasyNotif.Logging;

namespace Jellyfin.Plugin.EasyNotif.Tests.TestDoubles;

/// <summary>
/// An in-memory <see cref="IEasyNotifLog"/> that records every call, so tests can assert on
/// retro-instrumentation (which event, at which level, with which fields) without a real file or
/// Moq ceremony. <see cref="Tail"/> always returns an empty list.
/// </summary>
public sealed class FakeEasyNotifLog : IEasyNotifLog
{
    /// <summary>One recorded call.</summary>
    /// <param name="Level">"Debug", "Info", "Warn" or "Error".</param>
    /// <param name="EventType">The event type passed by the caller.</param>
    /// <param name="Fields">The fields passed by the caller, or null.</param>
    /// <param name="Exception">The exception passed by the caller, or null.</param>
    public sealed record Entry(string Level, string EventType, IReadOnlyDictionary<string, object?>? Fields, Exception? Exception);

    /// <summary>Gets the recorded calls, in order.</summary>
    public List<Entry> Entries { get; } = [];

    /// <inheritdoc/>
    public void Debug(string eventType, IReadOnlyDictionary<string, object?>? fields = null)
        => Entries.Add(new Entry("Debug", eventType, fields, null));

    /// <inheritdoc/>
    public void Info(string eventType, IReadOnlyDictionary<string, object?>? fields = null)
        => Entries.Add(new Entry("Info", eventType, fields, null));

    /// <inheritdoc/>
    public void Warn(string eventType, IReadOnlyDictionary<string, object?>? fields = null, Exception? exception = null)
        => Entries.Add(new Entry("Warn", eventType, fields, exception));

    /// <inheritdoc/>
    public void Error(string eventType, IReadOnlyDictionary<string, object?>? fields = null, Exception? exception = null)
        => Entries.Add(new Entry("Error", eventType, fields, exception));

    /// <inheritdoc/>
    public IReadOnlyList<string> Tail(int count) => [];

    /// <inheritdoc/>
    public void Dispose()
    {
    }
}
