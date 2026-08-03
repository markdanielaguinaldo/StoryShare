using Jellyfin.Plugin.StoryShare.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.StoryShare;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ArtworkProvider>();
        serviceCollection.AddSingleton<StoryCardRenderer>();
        serviceCollection.AddSingleton<VideoAnimationEncoder>();
        serviceCollection.AddSingleton<ShareTokenService>();
        serviceCollection.AddSingleton<InstagramStoryPublisher>();
        serviceCollection.AddHostedService<ScriptInjectionService>();
    }
}
