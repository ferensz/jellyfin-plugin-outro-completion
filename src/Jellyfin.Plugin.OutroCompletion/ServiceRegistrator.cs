using Jellyfin.Plugin.OutroCompletion.Configuration;
using Jellyfin.Plugin.OutroCompletion.Core;
using Jellyfin.Plugin.OutroCompletion.Playback;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Jellyfin.Plugin.OutroCompletion;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.TryAddSingleton(TimeProvider.System);
        serviceCollection.AddSingleton(provider => new UserPreferenceStore(Path.Combine(
            provider.GetRequiredService<IApplicationPaths>().PluginConfigurationsPath,
            "Jellyfin.Plugin.OutroCompletion.users.json")));
        serviceCollection.AddSingleton(provider => new SettingsService(
            () => Plugin.Instance.Configuration,
            provider.GetRequiredService<UserPreferenceStore>()));
        serviceCollection.AddSingleton<ICompletionSettingsProvider>(provider => provider.GetRequiredService<SettingsService>());
        serviceCollection.AddSingleton<IPlaybackRepository, JellyfinPlaybackRepository>();
        serviceCollection.AddSingleton<CompletionEngine>();
        serviceCollection.AddScoped<IEventConsumer<PlaybackStartEventArgs>, PlaybackEventConsumer>();
        serviceCollection.AddScoped<IEventConsumer<PlaybackStopEventArgs>, PlaybackEventConsumer>();
        serviceCollection.AddHostedService<UserDataInvalidationService>();
    }
}
