using Jellyfin.Plugin.EasyNotif.Configuration;
using Jellyfin.Plugin.EasyNotif.Email;
using Jellyfin.Plugin.EasyNotif.Inject;
using Jellyfin.Plugin.EasyNotif.IO;
using Jellyfin.Plugin.EasyNotif.Services;
using Jellyfin.Plugin.EasyNotif.Storage;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.EasyNotif;

/// <summary>
/// Registers plugin services into the Jellyfin DI container.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc/>
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IFileSystem, FileSystem>();
        serviceCollection.AddSingleton<IConfigAccessor, PluginConfigAccessor>();
        serviceCollection.AddSingleton<IPreferencesStore, PreferencesStore>();
        serviceCollection.AddSingleton<IPreferenceService, PreferenceService>();
        serviceCollection.AddHttpClient(ResendEmailSender.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
        serviceCollection.AddSingleton<SendRateLimiter>();
        serviceCollection.AddSingleton<IEmailSender, ResendEmailSender>();
        serviceCollection.AddSingleton<IQuotaGuard, QuotaGuard>();
        serviceCollection.AddSingleton<ISendLog, SendLog>();
        serviceCollection.AddSingleton<IFileTransformationDetector, FileTransformationDetector>();
        serviceCollection.AddHostedService<StartupService>();
    }
}
