using System.Text.Json;
using Jellyfin.Plugin.EasyNotif.IO;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EasyNotif.Storage;

/// <summary>
/// Base class for the plugin's small JSON state files under
/// <c>{DataPath}/Jellyfin.Plugin.EasyNotif/</c>.
/// <para>
/// Every store built on this type is thread-safe via a <see cref="ReaderWriterLockSlim"/>, writes
/// atomically (temp file then rename), holds its contents in memory (this plugin is the only
/// writer, see Synthese.md section 10) and self-heals a corrupt file by moving it aside with a
/// <c>.corrupt-&lt;timestamp&gt;</c> suffix and starting fresh - it never throws a parse or I/O error
/// out of construction. Keeping the whole footprint under <c>DataPath</c> means the plugin can be
/// removed with no trace by deleting one directory.
/// </para>
/// </summary>
/// <typeparam name="T">The on-disk document type. Must have a public parameterless constructor
/// whose result is the valid "empty" state.</typeparam>
public abstract class JsonFileStore<T> : IDisposable
    where T : class, new()
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly string _fileName;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger _logger;
    private readonly ReaderWriterLockSlim _lock = new();
    private T _cache;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="JsonFileStore{T}"/> class and loads the file.
    /// </summary>
    /// <param name="applicationPaths">Provides the application data directory path.</param>
    /// <param name="fileSystem">File system abstraction, for testability.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="fileName">The file name inside the plugin data directory (for example
    /// <c>preferences.json</c>).</param>
    protected JsonFileStore(IApplicationPaths applicationPaths, IFileSystem fileSystem, ILogger logger, string fileName)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        _fileSystem = fileSystem;
        _logger = logger;
        _fileName = fileName;
        Directory = Path.Combine(applicationPaths.DataPath, "Jellyfin.Plugin.EasyNotif");
        _filePath = Path.Combine(Directory, fileName);
        _fileSystem.CreateDirectory(Directory);
        _cache = Load();
    }

    /// <summary>Gets the plugin data directory (created at construction).</summary>
    protected string Directory { get; }

    /// <summary>Reads a projection of the in-memory document under a read lock.</summary>
    /// <typeparam name="TResult">The projection result type.</typeparam>
    /// <param name="projection">Maps the document to the value the caller wants. Must not leak a
    /// mutable reference to the cached instance.</param>
    /// <returns>The projected value.</returns>
    protected TResult Read<TResult>(Func<T, TResult> projection)
    {
        ArgumentNullException.ThrowIfNull(projection);

        _lock.EnterReadLock();
        try
        {
            return projection(_cache);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Applies a mutation to the in-memory document under a write lock. The mutation returns true if
    /// it changed anything; only then is the file rewritten.
    /// </summary>
    /// <param name="mutation">The mutation to apply; returns true when it modified the document.</param>
    protected void Mutate(Func<T, bool> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);

        _lock.EnterWriteLock();
        try
        {
            if (mutation(_cache))
            {
                Write(_cache);
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>Serializes the document to the text written to disk. Override for a non-JSON layout.</summary>
    /// <param name="value">The document.</param>
    /// <returns>The file text.</returns>
    protected virtual string Serialize(T value) => JsonSerializer.Serialize(value, SerializerOptions);

    /// <summary>Parses the file text back into a document. Override for a non-JSON layout.</summary>
    /// <param name="raw">The file text.</param>
    /// <returns>The document, or a fresh instance when the text is empty.</returns>
    protected virtual T Deserialize(string raw) => JsonSerializer.Deserialize<T>(raw) ?? new T();

    private T Load()
    {
        if (!_fileSystem.FileExists(_filePath))
        {
            return new T();
        }

        try
        {
            return Deserialize(_fileSystem.ReadAllText(_filePath));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Never let a corrupt file crash the server. Back it up next to the original so it can
            // be inspected, then start fresh.
            _logger.LogError(ex, "[EasyNotif] {FileName} is unreadable; backing it up and starting fresh.", _fileName);
            TryBackupCorruptFile();
            return new T();
        }
    }

    private void Write(T value)
    {
        var tmp = _filePath + ".tmp";
        _fileSystem.WriteAllText(tmp, Serialize(value));
        _fileSystem.Move(tmp, _filePath, overwrite: true);
    }

    private void TryBackupCorruptFile()
    {
        try
        {
            var backup = $"{_filePath}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
            _fileSystem.Move(_filePath, backup, overwrite: false);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "[EasyNotif] Could not back up the corrupt {FileName}.", _fileName);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases resources held by the store.</summary>
    /// <param name="disposing">True when called from <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _lock.Dispose();
        }

        _disposed = true;
    }
}
