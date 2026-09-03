using System.Text.Json;
using Jellyfin.Plugin.EasyNotif.IO;
using Jellyfin.Plugin.EasyNotif.Models;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EasyNotif.Storage;

/// <summary>
/// The single source of persistence for user email preferences: reads and writes
/// <c>{DataPath}/Jellyfin.Plugin.EasyNotif/preferences.json</c>.
/// <para>
/// Thread-safe via a <see cref="ReaderWriterLockSlim"/>; writes are atomic (temp file then rename).
/// The file is held in memory (this plugin is the only writer, see Synthese.md section 10): it is
/// loaded once at construction and every <see cref="ReadAll"/> serves that snapshot without
/// touching the disk. Kept under <c>DataPath</c> (not <c>PluginConfigurationsPath</c>) so the whole
/// plugin footprint is one directory that can be deleted on uninstall with no trace.
/// </para>
/// </summary>
public sealed class PreferencesStore : IPreferencesStore, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _directory;
    private readonly string _filePath;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<PreferencesStore> _logger;
    private readonly ReaderWriterLockSlim _lock = new();
    private PreferencesFile _cache;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="PreferencesStore"/> class.
    /// </summary>
    /// <param name="applicationPaths">Provides the application data directory path.</param>
    /// <param name="fileSystem">File system abstraction, for testability.</param>
    /// <param name="logger">Logger.</param>
    public PreferencesStore(IApplicationPaths applicationPaths, IFileSystem fileSystem, ILogger<PreferencesStore> logger)
    {
        _fileSystem = fileSystem;
        _logger = logger;
        _directory = Path.Combine(applicationPaths.DataPath, "Jellyfin.Plugin.EasyNotif");
        _filePath = Path.Combine(_directory, "preferences.json");
        _fileSystem.CreateDirectory(_directory);
        _cache = Load();
    }

    /// <inheritdoc/>
    public IReadOnlyList<UserPreference> ReadAll()
    {
        _lock.EnterReadLock();
        try
        {
            return [.. _cache.Users];
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <inheritdoc/>
    public void Mutate(Func<List<UserPreference>, bool> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);

        _lock.EnterWriteLock();
        try
        {
            if (mutation(_cache.Users))
            {
                Write(_cache);
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    private PreferencesFile Load()
    {
        if (!_fileSystem.FileExists(_filePath))
        {
            return new PreferencesFile();
        }

        try
        {
            var json = _fileSystem.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<PreferencesFile>(json) ?? new PreferencesFile();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Never let a corrupt file crash the server. Back it up next to the original so it can
            // be inspected, then start fresh.
            _logger.LogError(ex, "[EasyNotif] preferences.json is unreadable; backing it up and starting fresh.");
            TryBackupCorruptFile();
            return new PreferencesFile();
        }
    }

    private void Write(PreferencesFile file)
    {
        var tmp = _filePath + ".tmp";
        _fileSystem.WriteAllText(tmp, JsonSerializer.Serialize(file, SerializerOptions));
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
            _logger.LogWarning(ex, "[EasyNotif] Could not back up the corrupt preferences.json.");
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _lock.Dispose();
        _disposed = true;
    }
}
