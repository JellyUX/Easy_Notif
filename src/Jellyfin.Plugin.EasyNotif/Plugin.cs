using Jellyfin.Plugin.EasyNotif.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.EasyNotif;

/// <summary>
/// Easy Notif plugin entry point.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasPluginConfiguration, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the singleton plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc/>
    public override string Name => "Easy Notif";

    /// <inheritdoc/>
    public override Guid Id => Guid.Parse("7a27339e-e774-4969-9755-8cd213dcc5e7");

    /// <inheritdoc/>
    public override string Description =>
        "Internal email notification service: scheduled new-media newsletters, personalised weekly watch recaps and manual admin emails.";

    /// <inheritdoc/>
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            DisplayName = Name,
            EnableInMainMenu = true,
            EmbeddedResourcePath = $"{GetType().Namespace}.Web.config.html"
        };
    }
}
