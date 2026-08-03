using Jellyfin.Plugin.StoryShare.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.StoryShare;

/// <summary>
/// Turns a Jellyfin library item into a 1080x1920 Instagram Story card.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Stable plugin id. Also used by the config page to load/save settings.
    /// </summary>
    public const string PluginGuid = "b6e3a1c4-5d27-4f8a-9c31-7a0f2d84e5b9";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Story Share";

    public override Guid Id => Guid.Parse(PluginGuid);

    public override string Description =>
        "Share movies, shows and music from your library as an Instagram Story card.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
        };
    }
}
