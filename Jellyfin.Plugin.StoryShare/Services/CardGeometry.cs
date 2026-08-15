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

    /// <summary>
    /// The sub-rect of <paramref name="bitmap"/> that fills <paramref name="dest"/>
    /// without distortion. <paramref name="biasY"/> chooses where a vertical crop
    /// takes its slice from — 0.5 is centred, lower is nearer the top, which is
    /// where a poster keeps its faces.
    /// </summary>
    public static SKRect CoverSourceRect(SKBitmap bitmap, SKRect dest, float biasY = 0.5f)
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
        var y = (bitmap.Height - h) * Math.Clamp(biasY, 0f, 1f);
        return SKRect.Create(0, y, bitmap.Width, h);
    }

    /// <summary>
    /// How far <paramref name="bitmap"/> and <paramref name="dest"/> are from
    /// being the same shape, as a factor of one — 1 means identical, 2 means one
    /// is twice as wide relative to its height as the other.
    /// </summary>
    public static float AspectMismatch(SKBitmap bitmap, SKRect dest)
    {
        if (dest.Height <= 0f || bitmap.Height <= 0)
        {
            return 1f;
        }

        var ratio = (bitmap.Width / (float)bitmap.Height) / (dest.Width / dest.Height);
        return ratio < 1f ? 1f / ratio : ratio;
    }

    /// <summary>
    /// Past this much shape difference, cropping the cover to fill the window
    /// throws away most of the picture — a 2:3 poster in a ticket's landscape band
    /// keeps a middle slice with no title and no faces. Anything above this is
    /// fitted whole into the window instead.
    /// </summary>
    public const float FitThreshold = 1.25f;

    /// <summary>
    /// The largest centred rect inside <paramref name="dest"/> with the bitmap's own
    /// aspect, shrunk by <paramref name="margin"/> so a push-in has somewhere to go
    /// without the picture ever being clipped.
    /// </summary>
    public static SKRect ContainRect(SKBitmap bitmap, SKRect dest, float margin = 1f)
    {
        var srcAspect = bitmap.Width / (float)bitmap.Height;

        var width = dest.Width;
        var height = width / srcAspect;
        if (height > dest.Height)
        {
            height = dest.Height;
            width = height * srcAspect;
        }

        width *= margin;
        height *= margin;

        return SKRect.Create(dest.MidX - (width / 2f), dest.MidY - (height / 2f), width, height);
    }

    /// <summary>Grows or shrinks a rect about its own centre.</summary>
    public static SKRect ScaleAbout(SKRect rect, float factor)
    {
        var width = rect.Width * factor;
        var height = rect.Height * factor;
        return SKRect.Create(rect.MidX - (width / 2f), rect.MidY - (height / 2f), width, height);
    }
}
