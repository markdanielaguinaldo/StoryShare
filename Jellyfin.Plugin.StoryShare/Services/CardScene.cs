using Jellyfin.Plugin.StoryShare.Configuration;
using SkiaSharp;

namespace Jellyfin.Plugin.StoryShare.Services;

/// <summary>
/// Everything needed to draw one card, prepared once. <see cref="Draw"/> may be
/// called repeatedly with a phase in [0,1) to produce a seamless loop, which is
/// what makes the animated version cheap: the artwork, the expensive background
/// blur and the laid-out text are all prepared a single time and reused.
///
/// Only three things ever move — the background pans, the artwork pushes in (or
/// spins, on Vinyl) and a light sweep crosses it. Everything else is baked flat
/// into <see cref="DecorLayer"/> (drawn under the artwork) and
/// <see cref="TextLayer"/> (drawn over it).
/// </summary>
internal sealed class CardScene : IDisposable
{
    public required CardTheme Theme { get; init; }

    public required Palette Palette { get; init; }

    public required SKImage TextLayer { get; init; }

    public SKBitmap? Art { get; init; }

    public SKBitmap? Backdrop { get; init; }

    public SKImage? ArtImage { get; init; }

    /// <summary>Blurred, oversized backdrop. Null means the flat gradient is used instead.</summary>
    public SKImage? BackgroundLayer { get; init; }

    /// <summary>Static decoration drawn beneath the artwork — the record's base, the fanned-out stack.</summary>
    public SKImage? DecorLayer { get; init; }

    /// <summary>
    /// Vector decoration drawn beneath the artwork, every frame, inside the tilt.
    ///
    /// Anything with a hard straight edge belongs here rather than in a baked layer:
    /// a rotated bitmap is resampled, and even with linear filtering its edges soften
    /// or stair-step. Redrawing the shape under the same transform keeps it crisp,
    /// and simple geometry is cheap enough to repeat per frame.
    /// </summary>
    public Action<SKCanvas>? DrawUnderArt { get; init; }

    /// <summary>
    /// Vector decoration drawn over the artwork every frame, given the loop phase.
    ///
    /// This is the way to move something other than the artwork itself — the
    /// cassette's hubs turn while its label stays put. Whatever it draws still has
    /// to be back where it started at phase 1, so a rotation here must be a whole
    /// number of turns per loop, exactly as <see cref="Spin"/> is.
    /// </summary>
    public Action<SKCanvas, float>? DrawOverArt { get; init; }

    /// <summary>Baked layer drawn over the artwork but still inside the tilt.</summary>
    public SKImage? TiltedOverlay { get; init; }

    public SKImage? ShadowLayer { get; init; }

    public SKRect ArtRect { get; init; }

    public float ArtCorner { get; init; } = 28f;

    /// <summary>Corner radius equal to half the side turns the clip into a circle.</summary>
    public bool ArtBorder { get; init; } = true;

    public bool Sweep { get; init; } = true;

    /// <summary>Rotate the artwork a full turn per loop, rather than pushing in.</summary>
    public bool Spin { get; init; }

    /// <summary>Degrees the decor layer and artwork are rotated by together.</summary>
    public float Tilt { get; init; }

    public SKPoint TiltPivot { get; init; }

    public void Draw(SKCanvas canvas, float phase)
    {
        // Sinusoidal easing: 0 -> 1 -> 0 across the loop, so the last frame lands
        // exactly where the first began and the video cycles without a jump.
        var swell = (1f - MathF.Cos(2f * MathF.PI * phase)) / 2f;

        canvas.Clear(SKColors.Black);
        DrawBackground(canvas, phase);

        var tilted = MathF.Abs(Tilt) > 0.01f;
        if (tilted)
        {
            canvas.Save();
            canvas.RotateDegrees(Tilt, TiltPivot.X, TiltPivot.Y);
        }

        // Linear rather than the default nearest sampling: under the tilt these
        // layers are resampled, and nearest turns every edge into a staircase.
        if (DecorLayer is not null)
        {
            canvas.DrawImage(DecorLayer, 0, 0, Card.FrameSampling);
        }

        DrawUnderArt?.Invoke(canvas);

        if (!ArtRect.IsEmpty && Art is not null)
        {
            DrawArtPanel(canvas, swell, phase);
        }

        DrawOverArt?.Invoke(canvas, phase);

        if (TiltedOverlay is not null)
        {
            canvas.DrawImage(TiltedOverlay, 0, 0, Card.FrameSampling);
        }

        if (tilted)
        {
            canvas.Restore();
        }

        canvas.DrawImage(TextLayer, 0, 0, Card.FrameSampling);
    }

    public void Dispose()
    {
        TextLayer.Dispose();
        BackgroundLayer?.Dispose();
        DecorLayer?.Dispose();
        TiltedOverlay?.Dispose();
        ArtImage?.Dispose();
        ShadowLayer?.Dispose();
        Art?.Dispose();
        Backdrop?.Dispose();
    }

    private void DrawBackground(SKCanvas canvas, float phase)
    {
        var full = new SKRect(0, 0, Card.Width, Card.Height);
        var background = Palette.Background;

        if (BackgroundLayer is null)
        {
            using (var paint = new SKPaint
            {
                Shader = SKShader.CreateLinearGradient(
                    new SKPoint(0, 0),
                    new SKPoint(0, Card.Height),
                    new[] { background.Top, background.Mid, background.Bottom },
                    new[] { 0f, 0.5f, 1f },
                    SKShaderTileMode.Clamp)
            })
            {
                canvas.DrawRect(full, paint);
            }

            // A flat ramp alone reads as a wall; the vignette gives the card a centre.
            using var vignette = new SKPaint
            {
                Shader = SKShader.CreateRadialGradient(
                    new SKPoint(Card.CenterX, Card.Height * 0.45f),
                    Card.Height * 0.62f,
                    new[] { background.Bottom.WithAlpha(0), background.Bottom.WithAlpha(110) },
                    new[] { 0.45f, 1f },
                    SKShaderTileMode.Clamp)
            };
            canvas.DrawRect(full, vignette);
            return;
        }

        // Pan and zoom within the oversized layer. The backdrop drifts downward
        // while the cover pushes in, which reads as depth rather than wobble.
        var swell = (1f - MathF.Cos(2f * MathF.PI * phase)) / 2f;
        var layerRect = new SKRect(0, 0, BackgroundLayer.Width, BackgroundLayer.Height);
        var zoom = Card.BackgroundOversample * (1f + (0.06f * swell));
        var drift = (layerRect.Height - (layerRect.Height / zoom)) * 0.22f * MathF.Cos(2f * MathF.PI * phase);
        var source = Card.Inset(layerRect, zoom, drift);

        canvas.DrawImage(BackgroundLayer, source, full, Card.FrameSampling, null);

        var scrimStops = Theme == CardTheme.FullBleed
            ? new[] { 0f, 0.35f, 1f }
            : new[] { 0f, 0.5f, 1f };
        var scrimColors = Theme == CardTheme.FullBleed
            ? new[] { new SKColor(0, 0, 0, 140), new SKColor(0, 0, 0, 60), new SKColor(0, 0, 0, 240) }
            : new[] { new SKColor(0, 0, 0, 190), new SKColor(0, 0, 0, 150), new SKColor(0, 0, 0, 225) };

        using (var scrim = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(0, Card.Height),
                scrimColors,
                scrimStops,
                SKShaderTileMode.Clamp)
        })
        {
            canvas.DrawRect(full, scrim);
        }

        if (background.IsAuto)
        {
            using var tint = new SKPaint
            {
                Color = Palette.Accent.WithAlpha(46),
                BlendMode = SKBlendMode.Overlay
            };
            canvas.DrawRect(full, tint);
            return;
        }

        // A chosen preset has to survive on a photographic theme too. Colour blend
        // keeps the backdrop's luminance — so the scrim still guarantees readable
        // text — while pulling its hue all the way onto the preset.
        using (var recolour = new SKPaint
        {
            Color = background.Top.WithAlpha(170),
            BlendMode = SKBlendMode.Color
        })
        {
            canvas.DrawRect(full, recolour);
        }

        using var deepen = new SKPaint { Color = background.Bottom.WithAlpha(48) };
        canvas.DrawRect(full, deepen);
    }

    private void DrawArtPanel(SKCanvas canvas, float swell, float phase)
    {
        var rounded = new SKRoundRect(ArtRect, ArtCorner);

        if (ShadowLayer is not null)
        {
            canvas.DrawImage(ShadowLayer, ArtRect.Left - Card.ShadowPad, ArtRect.Top - Card.ShadowPad);
        }

        canvas.Save();
        canvas.ClipRoundRect(rounded, antialias: true);

        if (ArtImage is not null)
        {
            var cover = Card.CoverSourceRect(Art!, ArtRect);

            if (Spin)
            {
                // The clip is already a circle, and a circle is rotation-invariant,
                // so the square of artwork keeps covering it at every angle.
                canvas.Save();
                canvas.RotateDegrees(360f * phase, ArtRect.MidX, ArtRect.MidY);
                canvas.DrawImage(ArtImage, cover, ArtRect, Card.FrameSampling, null);
                canvas.Restore();
            }
            else
            {
                // The frame stays put and the image pushes in behind it — a Ken Burns
                // move, rather than the whole panel growing.
                var source = Card.Inset(cover, 1f + (0.05f * swell));
                canvas.DrawImage(ArtImage, source, ArtRect, Card.FrameSampling, null);
            }
        }

        if (Sweep)
        {
            DrawLightSweep(canvas, phase);
        }

        canvas.Restore();

        if (ArtBorder)
        {
            using var border = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 3f,
                IsAntialias = true,
                Color = Palette.Accent.WithAlpha(120)
            };
            canvas.DrawRoundRect(rounded, border);
        }
    }

    /// <summary>
    /// A soft diagonal gloss that crosses the artwork once per loop. It starts
    /// and ends fully outside the panel, so it never pops at the loop point.
    /// </summary>
    private void DrawLightSweep(SKCanvas canvas, float phase)
    {
        const float band = 150f;

        canvas.Save();
        canvas.RotateDegrees(18f, ArtRect.MidX, ArtRect.MidY);

        var travel = (ArtRect.Width * 1.8f) + (band * 2f);
        var x = ArtRect.MidX - (travel / 2f) + (travel * phase);

        using var shine = new SKPaint
        {
            BlendMode = SKBlendMode.Plus,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(x - band, 0),
                new SKPoint(x + band, 0),
                new[]
                {
                    SKColors.White.WithAlpha(0),
                    SKColors.White.WithAlpha(34),
                    SKColors.White.WithAlpha(0)
                },
                new[] { 0f, 0.5f, 1f },
                SKShaderTileMode.Clamp)
        };

        // Inflated so the rotated band still covers the panel's corners.
        var sweepArea = ArtRect;
        sweepArea.Inflate(ArtRect.Width, ArtRect.Height);
        canvas.DrawRect(sweepArea, shine);

        canvas.Restore();
    }
}
