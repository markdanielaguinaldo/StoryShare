using System.Security.Cryptography;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.StoryShare.Configuration;

public enum CardTheme
{
    /// <summary>Blurred artwork background, poster on top. The default.</summary>
    Poster = 0,

    /// <summary>The cover edge to edge, with a bottom gradient and text over it.</summary>
    FullBleed = 1,

    /// <summary>Flat colour derived from the artwork, no photographic background.</summary>
    Minimal = 2,

    /// <summary>Cover in a tilted paper frame, caption printed on the paper below it.</summary>
    Polaroid = 3,

    /// <summary>Cover cut into a record — grooves, label ring and spindle hole.</summary>
    Vinyl = 4,

    /// <summary>Cover fanned out as a pile of cards, the front one face up.</summary>
    Stack = 5,

    /// <summary>Cinema ticket stub — perforated tear-off, cover across its head.</summary>
    Ticket = 6,

    /// <summary>Cover as a cassette sleeve, the tape sliding out of it.</summary>
    Cassette = 7,

    /// <summary>Review card — poster, a five-star row, and the caption as the review.</summary>
    Review = 8
}

/// <summary>
/// How an animated card moves. Only the video uses this — a still is always the
/// scene at phase 0, so every one of these looks identical as an image.
/// </summary>
public enum CardAnimation
{
    /// <summary>What the style does by itself: push in, spin on Vinyl, hubs on Cassette.</summary>
    Auto = 0,

    /// <summary>The whole card drifts through a slow figure of eight.</summary>
    Float = 1,

    /// <summary>Two beats per loop: the artwork swells and an accent glow breathes behind it.</summary>
    Pulse = 2
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

    /// <summary>
    /// Movement the animated card falls back to. Preselected in the Story dialog,
    /// where it is only offered once the format is set to video.
    /// </summary>
    public CardAnimation Animation { get; set; } = CardAnimation.Auto;

    /// <summary>
    /// Small line of text at the bottom of the card. Empty hides it.
    ///
    /// <c>{server}</c> is replaced with the Jellyfin server's own name, which is why
    /// the default is not a literal: this shipped hardcoded to one server's name, so
    /// every other install put a stranger's server on its cards until someone noticed.
    /// </summary>
    public string FooterText { get; set; } = "Now playing in {server}";

    /// <summary>The literal this plugin used to ship as the default, migrated on load.</summary>
    public const string LegacyFooterText = "Now playing in Project Mark";

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

    // ----- Rendering cache -----

    /// <summary>
    /// Keeps finished cards in memory so re-opening a share link, or flipping back
    /// to a style already previewed, does not redraw. A Vinyl video costs ~17s of
    /// drawing plus two ffmpeg passes, and the dialog asks for the same card twice
    /// — once for the preview, once when the link is opened.
    /// </summary>
    public bool CacheEnabled { get; set; } = true;

    /// <summary>Memory the cache may hold, in MB. Oldest-used cards are dropped first.</summary>
    public int CacheSizeMb { get; set; } = 64;
}
