using Jellyfin.Plugin.StoryShare.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.StoryShare;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ArtworkProvider>();
        serviceCollection.AddSingleton<CardCache>();
        serviceCollection.AddSingleton<StoryCardRenderer>();
        serviceCollection.AddSingleton<VideoAnimationEncoder>();
        serviceCollection.AddSingleton<ShareTokenService>();

        // Puts the Story button's script tag into index.html as it is served, so
        // the plugin never has to write to the jellyfin-web directory.
        serviceCollection.AddSingleton<IStartupFilter, ClientScriptStartupFilter>();
        serviceCollection.AddHostedService<LegacyTagCleanupService>();
    }
}
