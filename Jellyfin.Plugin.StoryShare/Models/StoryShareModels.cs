using Jellyfin.Plugin.StoryShare.Configuration;

namespace Jellyfin.Plugin.StoryShare.Models;

/// <summary>
/// Geometry and timing of the animated card.
///
/// Full 1080x1920 at 30 fps, because this feeds Instagram Stories. Only one loop
/// is rendered and ffmpeg repeats it <see cref="LoopCount"/> times, so a six
/// second clip still costs two seconds of drawing.
/// </summary>
public sealed record AnimationSpec(int Width, int Height, int FrameCount, double Fps, int LoopCount)
{
    /// <summary>Two second loop, repeated three times.</summary>
    public static AnimationSpec Video { get; } = new(1080, 1920, 60, 30d, 3);

    /// <summary>
    /// Vinyl's loop, six seconds of it.
    ///
    /// The record turns exactly once per loop, because a rotation only meets itself
    /// at the seam after a whole number of turns — so the only way to slow the spin
    /// down is to lengthen the loop. Six seconds puts it at 10 rpm, and the extra
    /// frames are why a Vinyl video costs roughly 17s of drawing against 7s for the
    /// rest. 24 fps rather than 30 keeps that bill down without visible judder on a
    /// movement this slow.
    /// </summary>
    public static AnimationSpec Spin { get; } = new(1080, 1920, 144, 24d, 2);

    public static AnimationSpec For(CardTheme theme) =>
        theme == CardTheme.Vinyl ? Spin : Video;

    /// <summary>Seconds of finished video.</summary>
    public double Duration => FrameCount / Fps * LoopCount;
}

/// <summary>Per-render overrides. Anything left null falls back to plugin config.</summary>
public class StoryCardOptions
{
    public CardTheme? Theme { get; set; }

    /// <summary>How the video moves. Ignored by a still, which is always phase 0.</summary>
    public CardAnimation? Animation { get; set; }

    public string? FooterText { get; set; }

    /// <summary>Free-text caption drawn under the title, e.g. "10/10 no notes".</summary>
    public string? Comment { get; set; }

    public string? AccentColor { get; set; }

    /// <summary>
    /// Background preset id ("midnight"), a hex colour ("#2E1A47"), or "auto" to
    /// derive it from the artwork. Null falls back to the plugin config.
    /// </summary>
    public string? Background { get; set; }
}

/// <summary>
/// The styles a card can be rendered in. Served to the settings page and the share
/// dialog so neither has to keep its own copy of the theme and palette lists.
/// </summary>
public class StyleOptionsResponse
{
    public IReadOnlyList<ThemeOption> Themes { get; set; } = Array.Empty<ThemeOption>();

    public IReadOnlyList<BackgroundOption> Backgrounds { get; set; } = Array.Empty<BackgroundOption>();

    /// <summary>Movements the video can be given. Empty is not expected; the dialog falls back.</summary>
    public IReadOnlyList<AnimationOption> Animations { get; set; } = Array.Empty<AnimationOption>();

    /// <summary>The theme the server falls back to, so the dialog can preselect it.</summary>
    public int DefaultTheme { get; set; }

    /// <summary>The background id the server falls back to.</summary>
    public string DefaultBackground { get; set; } = BackgroundPresets.Auto;

    /// <summary>The animation the server falls back to.</summary>
    public int DefaultAnimation { get; set; }
}

public class AnimationOption
{
    public int Value { get; set; }

    public string Label { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;
}

public class ThemeOption
{
    public int Value { get; set; }

    public string Label { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;
}

public class BackgroundOption
{
    public string Id { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    /// <summary>Top of the ramp, as hex — the UI paints its swatch from these two.</summary>
    public string Top { get; set; } = string.Empty;

    public string Bottom { get; set; } = string.Empty;

    /// <summary>True when the card flips to dark text on this background.</summary>
    public bool IsLight { get; set; }
}

/// <summary>
/// Caption text the dialog may offer. Empty means the item has nothing to suggest,
/// which is the common case for music.
/// </summary>
public class CaptionSuggestionResponse
{
    public string Tagline { get; set; } = string.Empty;
}

public class ShareLinkResponse
{
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Same card, but served as Content-Disposition: attachment. Navigating here
    /// gives a real browser download with a proper filename — unlike a blob URL,
    /// this works on every browser including mobile Safari.
    /// </summary>
    public string DownloadUrl { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    /// <summary>False when PublicBaseUrl is unset, meaning the link is LAN-only.</summary>
    public bool IsPubliclyReachable { get; set; }
}
