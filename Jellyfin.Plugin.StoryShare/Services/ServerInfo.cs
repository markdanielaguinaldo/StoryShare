using System.Text.RegularExpressions;
using MediaBrowser.Controller;

namespace Jellyfin.Plugin.StoryShare.Services;

/// <summary>
/// The Jellyfin server's own display name, and the footer placeholders that use it.
///
/// Exists as a seam rather than injecting <see cref="IServerApplicationHost"/> straight
/// into the renderer: the dev harness has no server to hand, and a whole application
/// host is not something worth stubbing to draw a card.
/// </summary>
public sealed class ServerInfo
{
    /// <summary>Shown when the server has no name and none can be read.</summary>
    public const string Fallback = "Jellyfin";

    /// <summary>Placeholder in the footer text, replaced with <see cref="Name"/>.</summary>
    private static readonly Regex ServerToken = new(@"\{server\}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Func<string?> _resolve;

    public ServerInfo(IServerApplicationHost host)
    {
        // Read per card, not captured once: renaming the server in the dashboard
        // then shows up on the next card without restarting anything.
        _resolve = () => host.FriendlyName;
    }

    /// <summary>For the dev harness, which has no application host.</summary>
    public ServerInfo(string name)
    {
        _resolve = () => name;
    }

    public string Name
    {
        get
        {
            string? name = null;
            try
            {
                name = _resolve();
            }
            catch (Exception)
            {
                // A card with "Jellyfin" on it beats a card that fails to render, and
                // the host can throw while the server is shutting down.
            }

            return string.IsNullOrWhiteSpace(name) ? Fallback : name.Trim();
        }
    }

    /// <summary>Substitutes <c>{server}</c> in footer text. Null and empty pass through
    /// untouched, because an empty footer is how the footer is switched off.</summary>
    public string? Expand(string? text) =>
        string.IsNullOrEmpty(text) ? text : ServerToken.Replace(text, Name);
}
