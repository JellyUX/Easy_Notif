using System.Globalization;
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
/// <remarks>
/// File format contract (covered by SemVer since 1.0.0):
/// <list type="bullet">
/// <item>Evolve a format by addition only: a new file, or a new optional property whose absence
/// means the previous behaviour. An older version ignores the unknown property, so a rollback reads
/// the file without loss.</item>
/// <item>The root <c>Schema</c> integer is raised only for a change an older version cannot read.
/// The store then overrides <see cref="CurrentSchema"/> and <see cref="Migrate"/>; the original file
/// is saved as <c>&lt;file&gt;.schema&lt;N&gt;.bak</c> right before the first write of the migrated
/// document.</item>
/// <item>A file with a <c>Schema</c> above <see cref="CurrentSchema"/> (written by a newer version)
/// is loaded read-only: every write throws <see cref="StoreReadOnlyException"/> and the file is never
/// renamed to <c>.corrupt-*</c> nor overwritten. Load-time repairs go through <see cref="Repair"/>
/// and stay in memory, so a read-only store still constructs.</item>
/// <item>Construction never writes the file: repairs are persisted by the next successful
/// <see cref="Mutate"/>.</item>
/// </list>
/// </remarks>
/// <typeparam name="T">The on-disk document type. Must have a public parameterless constructor
/// whose result is the valid "empty" state.</typeparam>
public abstract class JsonFileStore<T> : IDisposable
    where T : class, new()
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly string _fileName;
    private readonly IFileSystem _fileSystem;
    private readonly ReaderWriterLockSlim _lock = new();

    /// <summary>The store logger, so derived types (background drains) can report a swallowed error.</summary>
    protected readonly ILogger _logger;

    private T _cache;
    private bool _disposed;
    private bool _readOnly;
    private int _loadedSchema;
    private string? _pendingMigrationBackup;
    private bool _pendingRepair;

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

    /// <summary>Gets a value indicating whether the file was written by a newer version and every
    /// write is refused. Set once while loading, before construction returns.</summary>
    public bool IsReadOnly => _readOnly;

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
            if (_readOnly)
            {
                throw new StoreReadOnlyException(_fileName, _loadedSchema, CurrentSchema);
            }

            if (mutation(_cache) || _pendingRepair)
            {
                Write(_cache);
                _pendingRepair = false;
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Applies a load-time repair (seeding, sanitizing) to the in-memory document under a write lock,
    /// without writing the file: construction must never write, so a read-only file or an unwritable
    /// directory cannot break startup. A repair that changed anything is persisted by the next
    /// successful <see cref="Mutate"/>; on a read-only store it stays in memory only.
    /// </summary>
    /// <param name="repair">The repair to apply; returns true when it modified the document.</param>
    protected void Repair(Func<T, bool> repair)
    {
        ArgumentNullException.ThrowIfNull(repair);

        _lock.EnterWriteLock();
        try
        {
            if (repair(_cache) && !_readOnly)
            {
                _pendingRepair = true;
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

    /// <summary>Gets the schema version this build reads and writes. Raise it only with a
    /// <see cref="Migrate"/> override (see the format contract in the type remarks).</summary>
    protected virtual int CurrentSchema => 1;

    /// <summary>Reads the schema version of the file text: the root <c>Schema</c> integer, or 1 when
    /// it is absent (files written before the property existed). Override for a non-JSON layout.</summary>
    /// <param name="raw">The file text.</param>
    /// <returns>The schema version the file was written with.</returns>
    /// <exception cref="JsonException">The text is not JSON, or <c>Schema</c> is not an integer.</exception>
    protected virtual int ReadSchema(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("Schema", out var schema))
        {
            return 1;
        }

        if (schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version))
        {
            throw new JsonException("The Schema property is not an integer.");
        }

        return version;
    }

    /// <summary>Upgrades a document read from an older schema, in memory. The default returns it
    /// unchanged; an override must also set the document's <c>Schema</c> to <see cref="CurrentSchema"/>.</summary>
    /// <param name="document">The document deserialized from the older file.</param>
    /// <param name="fromSchema">The schema version the file was written with.</param>
    /// <returns>The migrated document.</returns>
    protected virtual T Migrate(T document, int fromSchema) => document;

    private T Load()
    {
        _loadedSchema = CurrentSchema;
        if (!_fileSystem.FileExists(_filePath))
        {
            return new T();
        }

        try
        {
            var raw = _fileSystem.ReadAllText(_filePath);
            _loadedSchema = ReadSchema(raw);
            if (_loadedSchema > CurrentSchema)
            {
                return LoadReadOnly(raw);
            }

            var document = Deserialize(raw);
            if (_loadedSchema < CurrentSchema)
            {
                document = Migrate(document, _loadedSchema);
                _pendingMigrationBackup = raw;
                _logger.LogInformation(
                    "[EasyNotif] Migrated {FileName} from schema {From} to {To} in memory.",
                    _fileName,
                    _loadedSchema,
                    CurrentSchema);
            }

            return document;
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

    private T LoadReadOnly(string raw)
    {
        // Written by a newer version (for example after a plugin rollback). Never rewrite nor
        // quarantine it: the newer version will read it again once reinstalled.
        _readOnly = true;
        _logger.LogError(
            "[EasyNotif] {FileName} has schema {Schema}, newer than the supported {Supported}; it is read-only until the plugin is updated.",
            _fileName,
            _loadedSchema,
            CurrentSchema);
        try
        {
            return Deserialize(raw);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "[EasyNotif] {FileName} could not be read with this version; serving an empty document.", _fileName);
            return new T();
        }
    }

    private void Write(T value)
    {
        if (_pendingMigrationBackup is not null)
        {
            var backup = $"{_filePath}.schema{_loadedSchema}.bak";
            if (!_fileSystem.FileExists(backup))
            {
                _fileSystem.WriteAllText(backup, _pendingMigrationBackup);
            }

            _pendingMigrationBackup = null;
        }

        var tmp = _filePath + ".tmp";
        _fileSystem.WriteAllText(tmp, Serialize(value));
        _fileSystem.Move(tmp, _filePath, overwrite: true);
        OnWritten(_filePath);
    }

    /// <summary>
    /// Called after the file has been written and renamed into place. The default does nothing;
    /// override to tighten file permissions or run another post-write side effect.
    /// </summary>
    /// <param name="filePath">The absolute path to the file just written.</param>
    protected virtual void OnWritten(string filePath)
    {
    }

    private void TryBackupCorruptFile()
    {
        try
        {
            // Keep only the most recent backup. A persistently corrupt file, or a disk that keeps
            // failing reads, must not accumulate one snapshot per restart - a preferences.json
            // backup holds cleartext addresses and a playback-history.json backup escapes the
            // retention window entirely.
            foreach (var stale in new List<string>(_fileSystem.EnumerateFiles(Directory, _fileName + ".corrupt-*")))
            {
                try
                {
                    _fileSystem.Delete(stale);
                }
                catch (IOException)
                {
                    // Best effort: a stale backup we cannot remove is not worth failing over.
                }
            }

            var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            var backup = Path.Combine(Directory, $"{_fileName}.corrupt-{stamp}");
            _fileSystem.Move(_filePath, backup, overwrite: false);
            _logger.LogWarning(
                "[EasyNotif] Backed up the corrupt {FileName} as {Backup} and started fresh.",
                _fileName,
                Path.GetFileName(backup));
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
