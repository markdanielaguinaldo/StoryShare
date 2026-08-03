using SkiaSharp;

namespace Jellyfin.Plugin.StoryShare.Services;

/// <summary>
/// Fixed geometry every card is laid out in. Instagram crops aggressively at the
/// top and bottom for its own chrome, so everything meaningful stays inside the
/// <see cref="SafeTop"/>..<see cref="SafeBottom"/> band.
/// </summary>
internal static class Card
{
    public const int Width = 1080;
    public const int Height = 1920;

    public const float SafeTop = 280f;
    public const float SafeBottom = 1660f;
    public const float FooterBaseline = 1740f;
    public const float CenterX = Width / 2f;
    public const float TextMaxWidth = 900f;

    /// <summary>How much larger than the canvas the background layer is rendered,
    /// so there is material to pan and zoom into without exposing an edge.</summary>
    public const float BackgroundOversample = 1.15f;

    /// <summary>Slack around a baked shadow, so the blur is not clipped.</summary>
    public const float ShadowPad = 120f;

    /// <summary>For one-off work that downscales heavily, where mipmaps pay off.</summary>
    public static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    /// <summary>
    /// For per-frame draws. Mipmaps are pointless here — the background is
    /// downscaled by only ~1.15x and the artwork is usually scaled up — and asking
    /// for them makes Skia build a mipmap chain on every single draw.
    /// </summary>
    public static readonly SKSamplingOptions FrameSampling = new(SKFilterMode.Linear, SKMipmapMode.None);

    /// <summary>Centred sub-rect, used to zoom into a source image.</summary>
    public static SKRect Inset(SKRect source, float zoom, float offsetY = 0f)
    {
        var width = source.Width / zoom;
        var height = source.Height / zoom;
        var x = source.MidX - (width / 2f);
        var y = source.MidY - (height / 2f) + offsetY;
        return SKRect.Create(x, y, width, height);
    }

    /// <summary>The sub-rect of <paramref name="bitmap"/> that fills <paramref name="dest"/> without distortion.</summary>
    public static SKRect CoverSourceRect(SKBitmap bitmap, SKRect dest)
    {
        var srcAspect = bitmap.Width / (float)bitmap.Height;
        var dstAspect = dest.Width / dest.Height;

        if (srcAspect > dstAspect)
        {
            var w = bitmap.Height * dstAspect;
            var x = (bitmap.Width - w) / 2f;
            return SKRect.Create(x, 0, w, bitmap.Height);
        }

        var h = bitmap.Width / dstAspect;
        var y = (bitmap.Height - h) / 2f;
        return SKRect.Create(0, y, bitmap.Width, h);
    }
}
