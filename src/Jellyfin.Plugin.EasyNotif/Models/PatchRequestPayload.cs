namespace Jellyfin.Plugin.EasyNotif.Models;

/// <summary>
/// Payload passed by the FileTransformation plugin to a transformation callback.
/// Deserialized from { "contents": "&lt;file content&gt;" }.
/// </summary>
public class PatchRequestPayload
{
    /// <summary>
    /// Gets or sets the raw file contents to transform.
    /// </summary>
    public string? Contents { get; set; }
}
