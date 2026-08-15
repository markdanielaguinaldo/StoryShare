using SkiaSharp;

namespace Jellyfin.Plugin.StoryShare.Configuration;

/// <summary>
/// A named background colour ramp. <see cref="Id"/> is what travels in the API
/// query string, the share token and the saved config, so ids must stay stable
/// once they have shipped — labels and colours can change freely.
/// </summary>
public sealed record BackgroundPreset(string Id, string Label, string Top, string Bottom, bool IsLight);

/// <summary>
/// The ramp a card is actually painted with, after the preset id (or a raw hex
/// colour, or "derive it from the artwork") has been resolved.
/// </summary>
public readonly record struct CardBackground(SKColor Top, SKColor Mid, SKColor Bottom, bool IsLight, bool IsAuto);

/// <summary>Description of one card style, for the settings page and the share dialog.</summary>
public sealed record CardThemeInfo(CardTheme Theme, string Label, string Description);

/// <summary>Description of one animation, for the settings page and the share dialog.</summary>
public sealed record CardAnimationInfo(CardAnimation Animation, string Label, string Description);

public static class CardAnimations
{
    public static IReadOnlyList<CardAnimationInfo> All { get; } = new CardAnimationInfo[]
    {
        new(CardAnimation.Auto, "Auto", "The style's own movement — a slow push in, or a turning record."),
        new(CardAnimation.Float, "Float", "The whole card drifts through a slow figure of eight."),
        new(CardAnimation.Pulse, "Pulse", "Two beats a loop, with an accent glow behind the artwork.")
    };
}

public static class CardThemes
{
    public static IReadOnlyList<CardThemeInfo> All { get; } = new CardThemeInfo[]
    {
        new(CardTheme.Poster, "Poster", "Cover on a blurred version of its own artwork."),
        new(CardTheme.FullBleed, "Full bleed", "Artwork fills the card, text over a gradient."),
        new(CardTheme.Minimal, "Minimal", "Flat background, no photographic backdrop."),
        new(CardTheme.Polaroid, "Polaroid", "Cover in a tilted paper frame with the caption underneath."),
        new(CardTheme.Vinyl, "Vinyl", "Cover cut into a record, grooves and all. Spins in the video."),
        new(CardTheme.Stack, "Stack", "Cover fanned out as a pile of cards, the front one face up."),
        new(CardTheme.Ticket, "Ticket", "Cinema ticket stub, perforated and torn along the bottom."),
        new(CardTheme.Cassette, "Cassette", "Cover as a tape label. The hubs turn in the video."),
        new(CardTheme.Review, "Review", "Framed review card with a five-star row and your caption.")
    };
}

public static class BackgroundPresets
{
    /// <summary>Derive the background from the item's own artwork — the original behaviour.</summary>
    public const string Auto = "auto";

    /// <summary>Use <see cref="PluginConfiguration.BackgroundColor"/> rather than a preset.</summary>
    public const string Custom = "custom";

    public static IReadOnlyList<BackgroundPreset> All { get; } = new BackgroundPreset[]
    {
        new("midnight", "Midnight", "#1B2A4A", "#05070E", false),
        new("ink", "Ink", "#161A22", "#04060A", false),
        new("slate", "Slate", "#35415A", "#0F131B", false),
        new("graphite", "Graphite", "#2F2F33", "#060607", false),
        new("ocean", "Ocean", "#0F4A64", "#03121B", false),
        new("teal", "Teal", "#0D4B45", "#03110F", false),
        new("forest", "Forest", "#1F4A33", "#05110B", false),
        new("olive", "Olive", "#46501F", "#0D1005", false),
        new("sand", "Sand", "#8A6A45", "#1B120A", false),
        new("ember", "Ember", "#7C3113", "#170804", false),
        new("crimson", "Crimson", "#6E1B2C", "#14060A", false),
        new("rose", "Rose", "#8C3A5A", "#1A0A11", false),
        new("plum", "Plum", "#4A2350", "#100613", false),
        new("royal", "Royal", "#2C2A72", "#08081A", false),
        new("paper", "Paper", "#F6F2E9", "#DCD3C1", true),
        new("bone", "Bone", "#EDEDEA", "#C6C6C1", true)
    };

    private static readonly Dictionary<string, BackgroundPreset> ById =
        All.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);

    public static BackgroundPreset? Find(string? id) =>
        !string.IsNullOrWhiteSpace(id) && ById.TryGetValue(id.Trim(), out var preset) ? preset : null;

    /// <summary>
    /// Turns a request ("midnight", "#2E1A47", "custom", "auto", or nothing at all)
    /// into the ramp to paint. Falls back through the plugin config and finally to
    /// the artwork-derived gradient, so an unknown id can never fail a render.
    /// </summary>
    public static CardBackground Resolve(string? request, PluginConfiguration config, SKColor accent)
    {
        var id = Clean(request) ?? Clean(config.Background) ?? Auto;

        if (string.Equals(id, Custom, StringComparison.OrdinalIgnoreCase))
        {
            id = Clean(config.BackgroundColor) ?? Auto;
        }

        // Presets are matched before hex so an id can never be misread as a colour.
        if (Find(id) is { } preset
            && SKColor.TryParse(preset.Top, out var top)
            && SKColor.TryParse(preset.Bottom, out var bottom))
        {
            return new CardBackground(top, ColorMath.Lerp(top, bottom, 0.55f), bottom, preset.IsLight, false);
        }

        // Any parseable colour works wherever a preset id does, which is what lets
        // the share dialog's colour picker send "#2E1A47" straight through.
        if (SKColor.TryParse(id, out var seed))
        {
            var seedBottom = ColorMath.Darken(seed, 0.12f);
            return new CardBackground(
                seed,
                ColorMath.Lerp(seed, seedBottom, 0.55f),
                seedBottom,
                ColorMath.IsLight(seed),
                false);
        }

        return new CardBackground(
            ColorMath.Darken(accent, 0.55f),
            ColorMath.Darken(accent, 0.18f),
            SKColors.Black,
            false,
            true);
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal static class ColorMath
{
    public static SKColor Darken(SKColor color, float amount)
    {
        color.ToHsl(out var h, out var s, out var l);
        return SKColor.FromHsl(h, s, Math.Clamp(l * amount, 0f, 100f));
    }

    public static SKColor Lerp(SKColor a, SKColor b, float t) => new(
        (byte)(a.Red + ((b.Red - a.Red) * t)),
        (byte)(a.Green + ((b.Green - a.Green) * t)),
        (byte)(a.Blue + ((b.Blue - a.Blue) * t)),
        (byte)(a.Alpha + ((b.Alpha - a.Alpha) * t)));

    /// <summary>Relative luminance, used to decide whether a card needs dark text.</summary>
    public static bool IsLight(SKColor color) =>
        ((0.2126 * color.Red) + (0.7152 * color.Green) + (0.0722 * color.Blue)) / 255.0 > 0.62;
}
