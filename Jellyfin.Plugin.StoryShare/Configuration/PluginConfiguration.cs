using System.Security.Cryptography;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.StoryShare.Configuration;

public enum CardTheme
{
    /// <summary>Blurred artwork background, poster on top. The default.</summary>
    Poster = 0,

    /// <summary>Full-bleed artwork with a bottom gradient and text over it.</summary>
    FullBleed = 1,

    /// <summary>Flat colour derived from the artwork, no photographic background.</summary>
    Minimal = 2,

    /// <summary>Cover in a tilted paper frame, caption printed on the paper below it.</summary>
    Polaroid = 3,

    /// <summary>Cover cut into a record — grooves, label ring and spindle hole.</summary>
    Vinyl = 4,

    /// <summary>Cover fanned out as a pile of cards, the front one face up.</summary>
    Stack = 5
}

public class PluginConfiguration : BasePluginConfiguration
{
    public PluginConfiguration()
    {
        // Signing key for share links. Generated once, then persisted with the config.
        SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    }

    // ----- Card appearance -----

    public CardTheme Theme { get; set; } = CardTheme.Poster;

    /// <summary>Small line of text at the bottom of the card. Empty hides it.</summary>
    public string FooterText { get; set; } = "Now playing in Project Mark";

    public bool ShowYear { get; set; } = true;

    public bool ShowGenres { get; set; } = true;

    public bool ShowRating { get; set; } = true;

    public bool ShowRuntime { get; set; } = true;

    /// <summary>Hex accent colour, e.g. "#00A4DC". Empty = derive from the artwork.</summary>
    public string AccentColor { get; set; } = string.Empty;

    /// <summary>
    /// Background preset id from <see cref="BackgroundPresets.All"/>, or
    /// <see cref="BackgroundPresets.Auto"/> to derive it from the artwork, or
    /// <see cref="BackgroundPresets.Custom"/> to use <see cref="BackgroundColor"/>.
    /// </summary>
    public string Background { get; set; } = BackgroundPresets.Auto;

    /// <summary>Hex background colour used when <see cref="Background"/> is "custom".</summary>
    public string BackgroundColor { get; set; } = string.Empty;

    // ----- Share links -----

    /// <summary>
    /// Externally reachable base URL of this server, e.g. https://jellyfin.example.com.
    /// Required for share links to work off-LAN.
    /// </summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>How long an unauthenticated share link stays valid.</summary>
    public int ShareLinkLifetimeMinutes { get; set; } = 60;

    /// <summary>HMAC key used to sign share links. Rotating it invalidates existing links.</summary>
    public string SigningKey { get; set; }

    // ----- Web UI integration -----

    /// <summary>
    /// Injects a small script into jellyfin-web's index.html that adds a
    /// "Share to Story" button to item detail pages.
    /// </summary>
    public bool InjectClientScript { get; set; } = true;
}
