namespace Jellyfin.Plugin.EasyNotif.Storage;

/// <summary>
/// Thrown on a write to a store whose file was written by a newer plugin version (a <c>Schema</c>
/// above the supported one). Derives from <see cref="IOException"/> so the existing storage error
/// paths (API 503, drain logging) handle it without a special case.
/// </summary>
public sealed class StoreReadOnlyException : IOException
{
    /// <summary>Initializes a new instance of the <see cref="StoreReadOnlyException"/> class.</summary>
    /// <param name="fileName">The store file name.</param>
    /// <param name="fileSchema">The schema version found in the file.</param>
    /// <param name="supportedSchema">The schema version this build supports.</param>
    public StoreReadOnlyException(string fileName, int fileSchema, int supportedSchema)
        : base($"{fileName} has schema {fileSchema}, newer than the supported {supportedSchema}; it is read-only.")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="StoreReadOnlyException"/> class.</summary>
    public StoreReadOnlyException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="StoreReadOnlyException"/> class.</summary>
    /// <param name="message">The error message.</param>
    public StoreReadOnlyException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="StoreReadOnlyException"/> class.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying error.</param>
    public StoreReadOnlyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
