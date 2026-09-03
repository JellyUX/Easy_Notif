using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.EasyNotif.Inject;

/// <summary>
/// Bridge to the FileTransformation plugin. See <see cref="FileTransformationDetector"/>.
/// </summary>
public interface IFileTransformationDetector
{
    /// <summary>
    /// Returns true when the FileTransformation plugin is loaded and its registration entry point
    /// is reachable.
    /// </summary>
    /// <returns>True when FileTransformation is available.</returns>
    bool IsAvailable();

    /// <summary>
    /// Registers a file transformation via the FileTransformation plugin. Only call after
    /// <see cref="IsAvailable"/> returns true.
    /// </summary>
    /// <param name="payload">
    /// A JObject matching TransformationRegistrationPayload (id, fileNamePattern, callbackAssembly,
    /// callbackClass, callbackMethod).
    /// </param>
    void RegisterTransformation(JObject payload);
}
