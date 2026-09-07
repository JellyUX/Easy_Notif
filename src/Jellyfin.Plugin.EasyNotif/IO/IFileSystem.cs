namespace Jellyfin.Plugin.EasyNotif.IO;

/// <summary>
/// Thin abstraction over System.IO file/directory access, so that file-touching code can be tested
/// without hitting the real disk. Ported from JellyUX Keep or Remove.
/// </summary>
public interface IFileSystem
{
    /// <summary>Determines whether the given file exists.</summary>
    /// <param name="path">The file path to check.</param>
    /// <returns>True if the file exists.</returns>
    bool FileExists(string path);

    /// <summary>Reads the entire contents of a file as text.</summary>
    /// <param name="path">The file path to read.</param>
    /// <returns>The file contents.</returns>
    string ReadAllText(string path);

    /// <summary>Writes text to a file, creating or overwriting it.</summary>
    /// <param name="path">The file path to write.</param>
    /// <param name="contents">The text to write.</param>
    void WriteAllText(string path, string contents);

    /// <summary>Moves (renames) a file, optionally overwriting the destination.</summary>
    /// <param name="sourceFileName">The source file path.</param>
    /// <param name="destFileName">The destination file path.</param>
    /// <param name="overwrite">Whether to overwrite the destination if it already exists.</param>
    void Move(string sourceFileName, string destFileName, bool overwrite);

    /// <summary>Deletes the given file.</summary>
    /// <param name="path">The file path to delete.</param>
    void Delete(string path);

    /// <summary>Creates the given directory, including any missing parent directories.</summary>
    /// <param name="path">The directory path to create.</param>
    void CreateDirectory(string path);

    /// <summary>Determines whether the given directory exists.</summary>
    /// <param name="path">The directory path to check.</param>
    /// <returns>True if the directory exists.</returns>
    bool DirectoryExists(string path);

    /// <summary>Returns the full paths of the files in a directory that match a search pattern.</summary>
    /// <param name="path">The directory to enumerate. An empty sequence when it does not exist.</param>
    /// <param name="searchPattern">A search pattern such as <c>*.html</c>.</param>
    /// <returns>The matching file paths.</returns>
    IEnumerable<string> EnumerateFiles(string path, string searchPattern);

    /// <summary>Gets the last-write time of a file, in UTC.</summary>
    /// <param name="path">The file path.</param>
    /// <returns>The last-write time (UTC).</returns>
    DateTime GetLastWriteTimeUtc(string path);
}
