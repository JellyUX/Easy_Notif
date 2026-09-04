using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EasyNotif.Tests.TestDoubles;

/// <summary>
/// An <see cref="ILogger{T}"/> that keeps every formatted message, so a test can assert on what was
/// (and was not) logged - used for the R15 "secrets never logged" checks.
/// </summary>
/// <typeparam name="T">The category type.</typeparam>
public sealed class CapturingLogger<T> : ILogger<T>
{
    /// <summary>Gets the recorded log entries.</summary>
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    /// <summary>Gets every recorded message, joined - convenient for "does not contain" asserts.</summary>
    public string AllText => string.Join("\n", Entries.Select(e => e.Message));

    /// <inheritdoc/>
    public IDisposable BeginScope<TState>(TState state)
        where TState : notnull => NullScope.Instance;

    /// <inheritdoc/>
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc/>
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception)));

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
