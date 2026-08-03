using System.Globalization;
using System.Text;
using Jellyfin.Plugin.StoryShare.Configuration;
using Jellyfin.Plugin.StoryShare.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Jellyfin.Plugin.StoryShare.Services;

/// <summary>
/// Composes the 1080x1920 story card in one of the styles in <see cref="CardTheme"/>.
///
/// A card is built once into a <see cref="CardScene"/> and can then be drawn at any
/// animation phase. Layout is the only part that differs per theme: each Build*
/// method decides where the artwork and the text go, bakes the static decoration
/// into a layer, and hands the result to the shared scene.
///
/// Text is drawn through SKFont rather than SKPaint: SkiaSharp 3 (which Jellyfin
/// 10.11 ships) removed TextSize/MeasureText/DrawText from SKPaint entirely.
/// </summary>
public class StoryCardRenderer
{
    public const int Width = Card.Width;
    public const int Height = Card.Height;

    private static readonly string[] PreferredFonts =
    {
        "Inter", "Segoe UI", "Roboto", "Helvetica Neue", "Noto Sans", "DejaVu Sans", "Liberation Sans", "Arial"
    };

    private readonly ArtworkProvider _artwork;
    private readonly ILogger<StoryCardRenderer> _logger;

    public StoryCardRenderer(ArtworkProvider artwork, ILogger<StoryCardRenderer> logger)
    {
        _artwork = artwork;
        _logger = logger;
    }

    /// <summary>Renders a single still frame.</summary>
    public async Task<byte[]> RenderAsync(
        BaseItem item,
        StoryCardOptions options,
        SKEncodedImageFormat format,
        CancellationToken cancellationToken)
    {
        using var scene = await BuildSceneAsync(item, options, cancellationToken).ConfigureAwait(false);
        using var surface = SKSurface.Create(new SKImageInfo(Card.Width, Card.Height, SKColorType.Rgba8888, SKAlphaType.Premul));

        scene.Draw(surface.Canvas, 0f);

        using var image = surface.Snapshot();
        using var data = image.Encode(format, format == SKEncodedImageFormat.Jpeg ? 92 : 100);
        return data.ToArray();
    }

    /// <summary>
    /// Writes the animation as raw RGBA frames to <paramref name="destination"/>,
    /// ready to be piped into ffmpeg's stdin.
    ///
    /// Deliberately not PNG files in a temp folder: encoding each frame and round
    /// tripping it through disk cost several seconds per card, and on a server
    /// directory those writes may also be picked over by antivirus. Raw pixels
    /// skip both the encoder and the filesystem.
    /// </summary>
    public async Task RenderFramesAsync(
        BaseItem item,
        StoryCardOptions options,
        AnimationSpec spec,
        Stream destination,
        CancellationToken cancellationToken)
    {
        using var scene = await BuildSceneAsync(item, options, cancellationToken).ConfigureAwait(false);

        var info = new SKImageInfo(spec.Width, spec.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);

        var scale = spec.Width / (float)Card.Width;
        var frame = new byte[info.BytesSize];

        for (var i = 0; i < spec.FrameCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var canvas = surface.Canvas;
            canvas.Save();
            // Everything is laid out in 1080x1920 space; scale the whole scene down
            // instead of recomputing the layout at the smaller size.
            canvas.Scale(scale, scale);
            scene.Draw(canvas, i / (float)spec.FrameCount);
            canvas.Restore();

            using (var pixmap = surface.PeekPixels())
            {
                pixmap.GetPixelSpan().CopyTo(frame);
            }

            await destination.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- scene setup

    /// <summary>Themes that paint a flat background instead of the item's own artwork.</summary>
    private static bool IsFlat(CardTheme theme) =>
        theme is CardTheme.Minimal or CardTheme.Polaroid or CardTheme.Vinyl or CardTheme.Stack;

    private async Task<CardScene> BuildSceneAsync(
        BaseItem item,
        StoryCardOptions options,
        CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var theme = options.Theme ?? config.Theme;
        var flat = IsFlat(theme);

        var art = await _artwork.GetPrimaryAsync(item, cancellationToken).ConfigureAwait(false);
        var backdrop = flat
            ? null
            : await _artwork.GetBackdropAsync(item, cancellationToken).ConfigureAwait(false);

        var accent = ResolveAccent(options.AccentColor ?? config.AccentColor, art);
        var background = BackgroundPresets.Resolve(options.Background, config, accent);

        // Only a flat theme exposes the background colour directly. Poster and Full
        // bleed keep their dark scrim, so their text stays light whatever the preset.
        var palette = new Palette(accent, background, flat && background.IsLight);

        using var bold = CreateTypeface(SKFontStyleWeight.Bold);
        using var regular = CreateTypeface(SKFontStyleWeight.Normal);

        var footerText = options.FooterText ?? config.FooterText;
        var context = new LayoutContext(item, options, config, palette, art, backdrop, footerText, bold, regular);

        return theme switch
        {
            CardTheme.Polaroid => BuildPolaroid(context),
            CardTheme.Vinyl => BuildVinyl(context),
            CardTheme.Stack => BuildStack(context),
            _ => BuildClassic(theme, context)
        };
    }

    /// <summary>Everything the per-theme layout methods need, so they take one argument.</summary>
    private sealed record LayoutContext(
        BaseItem Item,
        StoryCardOptions Options,
        PluginConfiguration Config,
        Palette Palette,
        SKBitmap? Art,
        SKBitmap? Backdrop,
        string? FooterText,
        SKTypeface Bold,
        SKTypeface Regular)
    {
        /// <summary>Where content has to stop to leave the footer its own room.</summary>
        public float ContentBottom(float footerGap = 100f) =>
            string.IsNullOrWhiteSpace(FooterText) ? Card.SafeBottom : Card.FooterBaseline - footerGap;
    }

    // ---------------------------------------------------------------- poster / full bleed / minimal

    private CardScene BuildClassic(CardTheme theme, LayoutContext context)
    {
        var spec = SpecFor(theme);
        var lines = BuildTextBlock(context, context.Palette, spec);
        var textHeight = TotalHeight(lines);

        var artRect = theme != CardTheme.FullBleed && context.Art is not null
            ? MeasureArtRect(context.Art)
            : SKRect.Empty;

        var contentBottom = context.ContentBottom();
        var available = contentBottom - Card.SafeTop;

        var gap = artRect.IsEmpty ? 0f : 78f;
        var totalHeight = artRect.Height + gap + textHeight;

        // Long titles wrap to three lines and would otherwise run into the footer.
        // The text is the payload, so the artwork gives up whatever height is needed.
        if (!artRect.IsEmpty && totalHeight > available)
        {
            artRect = ShrinkToFit(artRect, available - gap - textHeight, 240f);
            if (artRect.IsEmpty)
            {
                gap = 0f;
            }

            totalHeight = artRect.Height + gap + textHeight;
        }

        var cursorY = theme == CardTheme.FullBleed
            ? contentBottom - textHeight
            : Card.SafeTop + Math.Max(0f, (available - totalHeight) / 2f);

        if (!artRect.IsEmpty)
        {
            artRect = MoveTo(artRect, cursorY);
            cursorY = artRect.Bottom + gap;
        }

        // The blur is by far the most expensive step, so bake the background once
        // into an oversized layer that later frames simply pan and zoom within.
        // Minimal shares this layout but not the photographic backdrop, so it gets
        // no layer at all and the scene falls through to the flat gradient.
        var backgroundLayer = IsFlat(theme)
            ? null
            : BuildBackgroundLayer(theme != CardTheme.FullBleed, context.Backdrop ?? context.Art);

        var textLayer = BuildOverlayLayer(lines, cursorY, Footer(context), null);
        Dispose(lines);

        return new CardScene
        {
            Theme = theme,
            Palette = context.Palette,
            Art = context.Art,
            Backdrop = context.Backdrop,
            BackgroundLayer = backgroundLayer,
            ArtImage = ArtImageFor(context.Art, artRect),
            ShadowLayer = artRect.IsEmpty ? null : BuildShadowLayer(artRect, 28f),
            TextLayer = textLayer,
            ArtRect = artRect
        };
    }

    // ---------------------------------------------------------------- polaroid

    private CardScene BuildPolaroid(LayoutContext context)
    {
        const float PaperWidth = 830f;
        const float Padding = 46f;
        const float CaptionGap = 40f;
        const float BottomPadding = 54f;
        const float Tilt = -2.6f;

        var stock = PaperStock(context.Palette.Background);

        // The caption is printed on the card, so its contrast follows the paper
        // rather than the surround — dark type on pale stock, light type on a deep one.
        var paperPalette = context.Palette with { LightText = ColorMath.IsLight(stock.Top) };
        var lines = BuildTextBlock(context, paperPalette, SpecFor(CardTheme.Polaroid));
        var captionHeight = TotalHeight(lines);

        var available = context.ContentBottom(90f) - Card.SafeTop;
        var photoSize = context.Art is null ? 0f : PaperWidth - (Padding * 2);

        float PaperHeight(float photo) =>
            Padding + photo + (photo > 0 ? CaptionGap : 0f) + captionHeight + BottomPadding;

        // Tilting the card costs a little height; PaperWidth * sin(2.6°) covers it.
        var budget = available - 44f;
        if (PaperHeight(photoSize) > budget && photoSize > 0f)
        {
            photoSize = Math.Max(280f, photoSize - (PaperHeight(photoSize) - budget));
        }

        var paperHeight = PaperHeight(photoSize);
        var paperTop = Card.SafeTop + Math.Max(0f, (available - paperHeight) / 2f);
        var paperRect = SKRect.Create(Card.CenterX - (PaperWidth / 2f), paperTop, PaperWidth, paperHeight);

        var photoRect = photoSize > 0f
            ? SKRect.Create(Card.CenterX - (photoSize / 2f), paperTop + Padding, photoSize, photoSize)
            : SKRect.Empty;

        var captionTop = photoSize > 0f ? photoRect.Bottom + CaptionGap : paperTop + Padding;

        // Only the shadow and the caption are baked. The card's own edges are drawn
        // as geometry inside the tilt — see CardScene.DrawUnderArt for why.
        var shadowLayer = BuildPaperShadow(paperRect);
        var captionLayer = BuildOverlayLayer(lines, captionTop, null, null);
        Dispose(lines);

        // Only the footer sits outside the paper, so it is the whole overlay.
        var textLayer = BuildOverlayLayer(
            Array.Empty<IStoryLine>(),
            0f,
            Footer(context),
            null);

        return new CardScene
        {
            Theme = CardTheme.Polaroid,
            // The surround is pushed darker so a coloured card still separates from it.
            Palette = context.Palette with { Background = Recede(context.Palette.Background), LightText = false },
            Art = context.Art,
            ArtImage = ArtImageFor(context.Art, photoRect),
            DecorLayer = shadowLayer,
            DrawUnderArt = canvas => DrawPaper(canvas, paperRect, photoRect, stock),
            TiltedOverlay = captionLayer,
            TextLayer = textLayer,
            ArtRect = photoRect,
            ArtCorner = 4f,
            ArtBorder = false,
            Tilt = Tilt,
            TiltPivot = new SKPoint(Card.CenterX, paperRect.MidY)
        };
    }

    private readonly record struct PaperStockColors(SKColor Top, SKColor Bottom, SKColor Edge);

    /// <summary>
    /// The card's own colour. "Match the artwork" keeps the classic white stock;
    /// any chosen background becomes the card, lifted towards white so it still
    /// reads as paper rather than a flat swatch.
    /// </summary>
    private static PaperStockColors PaperStock(CardBackground background)
    {
        if (background.IsAuto)
        {
            return new PaperStockColors(
                new SKColor(0xFF, 0xFF, 0xFD),
                new SKColor(0xF1, 0xEC, 0xE1),
                new SKColor(0, 0, 0, 26));
        }

        var top = ColorMath.Lerp(background.Top, SKColors.White, 0.18f);
        return new PaperStockColors(
            top,
            ColorMath.Darken(top, 0.90f),
            ColorMath.IsLight(top) ? new SKColor(0, 0, 0, 30) : new SKColor(255, 255, 255, 36));
    }

    /// <summary>Pushes a ramp back so a card painted in the same colour still stands off it.</summary>
    private static CardBackground Recede(CardBackground background) =>
        background.IsAuto
            ? background
            : background with
            {
                Top = ColorMath.Darken(background.Top, 0.40f),
                Mid = ColorMath.Darken(background.Mid, 0.40f)
            };

    private static void DrawPaper(SKCanvas canvas, SKRect paper, SKRect photo, PaperStockColors stock)
    {
        var rounded = new SKRoundRect(paper, 12f);

        using (var fill = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(paper.Left, paper.Top),
                new SKPoint(paper.Right, paper.Bottom),
                new[] { stock.Top, stock.Bottom },
                null,
                SKShaderTileMode.Clamp)
        })
        {
            canvas.DrawRoundRect(rounded, fill);
        }

        using (var edge = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
            IsAntialias = true,
            Color = stock.Edge
        })
        {
            canvas.DrawRoundRect(rounded, edge);
        }

        if (!photo.IsEmpty)
        {
            // A dark plate under the photo, so artwork that fails to load reads as a
            // blank print rather than a hole in the card.
            using var plate = new SKPaint { IsAntialias = true, Color = new SKColor(0x14, 0x15, 0x18) };
            canvas.DrawRoundRect(new SKRoundRect(photo, 4f), plate);
        }
    }

    private static SKImage BuildPaperShadow(SKRect paper)
    {
        using var surface = SKSurface.Create(
            new SKImageInfo(Card.Width, Card.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        // Baked, unlike the card itself: a blur has no hard edge to stair-step, and
        // this is the expensive part.
        using var shadow = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 190),
            IsAntialias = true,
            ImageFilter = SKImageFilter.CreateDropShadowOnly(0, 26f, 32f, 32f, new SKColor(0, 0, 0, 190))
        };
        canvas.DrawRoundRect(new SKRoundRect(paper, 12f), shadow);

        return surface.Snapshot();
    }

    // ---------------------------------------------------------------- vinyl

    private CardScene BuildVinyl(LayoutContext context)
    {
        const float MaxDiameter = 760f;
        const float MinDiameter = 380f;
        const float Gap = 84f;

        var lines = BuildTextBlock(context, context.Palette, SpecFor(CardTheme.Vinyl));
        var textHeight = TotalHeight(lines);

        var available = context.ContentBottom() - Card.SafeTop;
        var diameter = Math.Clamp(Math.Min(MaxDiameter, available - Gap - textHeight), MinDiameter, MaxDiameter);

        var topY = Card.SafeTop + Math.Max(0f, (available - (diameter + Gap + textHeight)) / 2f);
        var discRect = SKRect.Create(Card.CenterX - (diameter / 2f), topY, diameter, diameter);

        var decorLayer = BuildDiscBase(discRect, context.Palette);
        var textLayer = BuildOverlayLayer(
            lines,
            discRect.Bottom + Gap,
            Footer(context),
            canvas => DrawGrooves(canvas, discRect, context.Palette));
        Dispose(lines);

        return new CardScene
        {
            Theme = CardTheme.Vinyl,
            Palette = context.Palette,
            Art = context.Art,
            ArtImage = ArtImageFor(context.Art, discRect),
            DecorLayer = decorLayer,
            TextLayer = textLayer,
            ArtRect = discRect,
            // Half the side turns the rounded-rect clip into a circle.
            ArtCorner = diameter / 2f,
            ArtBorder = false,
            Spin = true
        };
    }

    /// <summary>Glow, shadow and the record's own black plate — everything under the label art.</summary>
    private static SKImage BuildDiscBase(SKRect disc, Palette palette)
    {
        using var surface = SKSurface.Create(
            new SKImageInfo(Card.Width, Card.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var center = new SKPoint(disc.MidX, disc.MidY);
        var radius = disc.Width / 2f;

        using (var glow = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateRadialGradient(
                center,
                radius * 1.45f,
                new[] { palette.Accent.WithAlpha(90), palette.Accent.WithAlpha(0) },
                new[] { 0.55f, 1f },
                SKShaderTileMode.Clamp)
        })
        {
            canvas.DrawCircle(center, radius * 1.45f, glow);
        }

        using (var shadow = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(0, 0, 0, 200),
            ImageFilter = SKImageFilter.CreateDropShadowOnly(0, 22f, 30f, 30f, new SKColor(0, 0, 0, 190))
        })
        {
            canvas.DrawCircle(center, radius, shadow);
        }

        using (var plate = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateRadialGradient(
                center,
                radius,
                new[] { ColorMath.Darken(palette.Accent, 0.35f), new SKColor(8, 8, 10) },
                new[] { 0f, 1f },
                SKShaderTileMode.Clamp)
        })
        {
            canvas.DrawCircle(center, radius, plate);
        }

        return surface.Snapshot();
    }

    /// <summary>
    /// Grooves, label ring and spindle hole. All rotationally symmetric, so they can
    /// be baked once and left standing still while the artwork spins underneath.
    /// </summary>
    private static void DrawGrooves(SKCanvas canvas, SKRect disc, Palette palette)
    {
        const int Grooves = 22;

        var center = new SKPoint(disc.MidX, disc.MidY);
        var radius = disc.Width / 2f;

        using (var dark = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.4f,
            IsAntialias = true,
            Color = new SKColor(0, 0, 0, 70)
        })
        using (var light = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.1f,
            IsAntialias = true,
            Color = new SKColor(255, 255, 255, 26)
        })
        {
            for (var i = 0; i < Grooves; i++)
            {
                var grooveRadius = radius * (0.965f - (i / (float)(Grooves - 1) * 0.60f));
                canvas.DrawCircle(center, grooveRadius, dark);
                canvas.DrawCircle(center, grooveRadius - 2.4f, light);
            }
        }

        using (var rim = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 4f,
            IsAntialias = true,
            Color = palette.Accent.WithAlpha(150)
        })
        {
            canvas.DrawCircle(center, radius - 2f, rim);
        }

        // A ring rather than a filled label, so the cover art stays readable.
        using (var label = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3f,
            IsAntialias = true,
            Color = palette.Accent.WithAlpha(170)
        })
        {
            canvas.DrawCircle(center, radius * 0.30f, label);
        }

        var holeRadius = radius * 0.037f;
        using (var hole = new SKPaint { IsAntialias = true, Color = palette.Background.Bottom })
        {
            canvas.DrawCircle(center, holeRadius, hole);
        }

        using var holeEdge = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
            IsAntialias = true,
            Color = new SKColor(0, 0, 0, 120)
        };
        canvas.DrawCircle(center, holeRadius, holeEdge);
    }

    // ---------------------------------------------------------------- stack

    private CardScene BuildStack(LayoutContext context)
    {
        const float Gap = 96f;
        const float Corner = 24f;

        var lines = BuildTextBlock(context, context.Palette, SpecFor(CardTheme.Stack));
        var textHeight = TotalHeight(lines);

        var artRect = SKRect.Empty;
        if (context.Art is not null)
        {
            // The fanned cards need headroom above and to the sides, so the front one
            // gives up some size rather than the pile running into the text.
            var full = MeasureArtRect(context.Art);
            var width = full.Width * 0.86f;
            var height = full.Height * 0.86f;
            artRect = SKRect.Create(Card.CenterX - (width / 2f), 0, width, height);
        }

        var contentBottom = context.ContentBottom();
        var available = contentBottom - Card.SafeTop;
        var gap = artRect.IsEmpty ? 0f : Gap;
        var totalHeight = artRect.Height + gap + textHeight;

        if (!artRect.IsEmpty && totalHeight > available)
        {
            artRect = ShrinkToFit(artRect, available - gap - textHeight, 240f);
            if (artRect.IsEmpty)
            {
                gap = 0f;
            }

            totalHeight = artRect.Height + gap + textHeight;
        }

        var cursorY = Card.SafeTop + Math.Max(0f, (available - totalHeight) / 2f);
        if (!artRect.IsEmpty)
        {
            artRect = MoveTo(artRect, cursorY);
            cursorY = artRect.Bottom + gap;
        }

        var stackLayer = artRect.IsEmpty ? null : BuildStackLayer(context.Art!, artRect, context.Palette, Corner);
        var textLayer = BuildOverlayLayer(lines, cursorY, Footer(context), null);
        Dispose(lines);

        return new CardScene
        {
            Theme = CardTheme.Stack,
            Palette = context.Palette,
            Art = context.Art,
            ArtImage = ArtImageFor(context.Art, artRect),
            DecorLayer = stackLayer,
            ShadowLayer = artRect.IsEmpty ? null : BuildShadowLayer(artRect, Corner),
            TextLayer = textLayer,
            ArtRect = artRect,
            ArtCorner = Corner
        };
    }

    /// <summary>
    /// The cards behind the front one, fanned about the bottom edge so the pile
    /// splays upward. Baked: only the face-up card animates.
    /// </summary>
    private static SKImage BuildStackLayer(SKBitmap art, SKRect front, Palette palette, float corner)
    {
        // Farthest first, and dimmest — the pile has to read as depth, not as a
        // second copy of the cover competing with the one in focus.
        var fan = new[] { (Angle: 9f, Dim: (byte)165), (Angle: 4.5f, Dim: (byte)105) };

        using var surface = SKSurface.Create(
            new SKImageInfo(Card.Width, Card.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        using var image = SKImage.FromBitmap(art);
        var source = Card.CoverSourceRect(art, front);
        var rounded = new SKRoundRect(front, corner);

        foreach (var card in fan)
        {
            canvas.Save();
            canvas.RotateDegrees(card.Angle, front.MidX, front.Bottom);

            using (var shadow = new SKPaint
            {
                IsAntialias = true,
                Color = new SKColor(0, 0, 0, 180),
                ImageFilter = SKImageFilter.CreateDropShadowOnly(0, 16f, 22f, 22f, new SKColor(0, 0, 0, 170))
            })
            {
                canvas.DrawRoundRect(rounded, shadow);
            }

            canvas.Save();
            canvas.ClipRoundRect(rounded, antialias: true);
            canvas.DrawImage(image, source, front, Card.Sampling, null);

            using (var scrim = new SKPaint { Color = new SKColor(0, 0, 0, card.Dim) })
            {
                canvas.DrawRect(front, scrim);
            }

            canvas.Restore();

            using (var border = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 3f,
                IsAntialias = true,
                Color = palette.Accent.WithAlpha(80)
            })
            {
                canvas.DrawRoundRect(rounded, border);
            }

            canvas.Restore();
        }

        return surface.Snapshot();
    }

    // ---------------------------------------------------------------- shared layers

    /// <summary>Static text, decoration and footer, flattened into one transparent overlay.</summary>
    private SKImage BuildOverlayLayer(
        IEnumerable<IStoryLine> lines,
        float top,
        FooterSpec? footer,
        Action<SKCanvas>? decorate)
    {
        using var surface = SKSurface.Create(
            new SKImageInfo(Card.Width, Card.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        decorate?.Invoke(canvas);

        var cursorY = top;
        foreach (var line in lines)
        {
            line.Draw(canvas, cursorY);
            cursorY += line.Height + line.SpacingAfter;
        }

        if (footer is not null)
        {
            using var typeface = CreateTypeface(SKFontStyleWeight.Normal);
            using var font = new SKFont(typeface, 34f) { Edging = SKFontEdging.SubpixelAntialias };
            using var paint = new SKPaint { IsAntialias = true, Color = footer.Color };
            using var dot = new SKPaint { Color = footer.Accent, IsAntialias = true };

            // The text is nudged right to balance the dot sitting left of it.
            var textWidth = font.MeasureText(footer.Text);
            canvas.DrawCircle(Card.CenterX - (textWidth / 2f) - 26f, Card.FooterBaseline - 11f, 9f, dot);
            canvas.DrawText(footer.Text, Card.CenterX + 14f, Card.FooterBaseline, SKTextAlign.Center, font, paint);
        }

        return surface.Snapshot();
    }

    private sealed record FooterSpec(string Text, SKColor Color, SKColor Accent);

    private static FooterSpec? Footer(LayoutContext context) =>
        string.IsNullOrWhiteSpace(context.FooterText)
            ? null
            : new FooterSpec(context.FooterText, context.Palette.Footer, context.Palette.Accent);

    /// <summary>Pre-blurred drop shadow for a panel that never moves.</summary>
    private static SKImage BuildShadowLayer(SKRect rect, float corner)
    {
        var width = (int)MathF.Ceiling(rect.Width + (Card.ShadowPad * 2));
        var height = (int)MathF.Ceiling(rect.Height + (Card.ShadowPad * 2));

        using var surface = SKSurface.Create(
            new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);

        var local = SKRect.Create(Card.ShadowPad, Card.ShadowPad, rect.Width, rect.Height);
        using var paint = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 200),
            ImageFilter = SKImageFilter.CreateDropShadowOnly(0, 24f, 34f, 34f, new SKColor(0, 0, 0, 190))
        };
        surface.Canvas.DrawRoundRect(new SKRoundRect(local, corner), paint);

        return surface.Snapshot();
    }

    private static SKImage? BuildBackgroundLayer(bool blur, SKBitmap? source)
    {
        if (source is null)
        {
            return null;
        }

        var layerWidth = (int)(Card.Width * Card.BackgroundOversample);
        var layerHeight = (int)(Card.Height * Card.BackgroundOversample);

        using var surface = SKSurface.Create(
            new SKImageInfo(layerWidth, layerHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);

        var dest = new SKRect(0, 0, layerWidth, layerHeight);
        using (var image = SKImage.FromBitmap(source))
        using (var paint = new SKPaint())
        {
            if (blur)
            {
                paint.ImageFilter = SKImageFilter.CreateBlur(48f, 48f, SKShaderTileMode.Clamp);
            }

            canvas.DrawImage(image, Card.CoverSourceRect(source, dest), dest, Card.Sampling, paint);
        }

        return surface.Snapshot();
    }

    // ---------------------------------------------------------------- text block

    private static TextSpec SpecFor(CardTheme theme) => theme switch
    {
        CardTheme.Polaroid => new TextSpec
        {
            TitleMax = 58f,
            TitleMin = 32f,
            TitleLines = 2,
            TitleSpacing = 14f,
            SubtitleMax = 32f,
            SubtitleMin = 24f,
            CommentMax = 30f,
            MaxWidth = 700f
        },
        CardTheme.Vinyl => new TextSpec
        {
            TitleMax = 68f,
            TitleMin = 40f,
            TitleLines = 2,
            SubtitleMax = 38f,
            CommentMax = 34f,
            MaxWidth = 860f
        },
        _ => new TextSpec()
    };

    private static List<IStoryLine> BuildTextBlock(LayoutContext context, Palette palette, TextSpec spec)
    {
        var lines = new List<IStoryLine>();

        var title = string.IsNullOrWhiteSpace(context.Item.Name) ? "Untitled" : context.Item.Name;

        lines.Add(TextBlock.Fit(
            title,
            context.Bold,
            spec.TitleMax,
            spec.TitleMin,
            spec.TitleLines,
            palette.Title,
            spec.MaxWidth,
            palette.TextShadow,
            spec.TitleSpacing));

        var subtitle = BuildSubtitle(context.Item, context.Config);
        if (!string.IsNullOrEmpty(subtitle))
        {
            lines.Add(TextBlock.Fit(
                subtitle,
                context.Regular,
                spec.SubtitleMax,
                spec.SubtitleMin,
                2,
                palette.Subtitle,
                spec.MaxWidth,
                palette.TextShadow * 0.75f,
                26f));
        }

        var facts = BuildFacts(context.Item, context.Config);
        if (spec.Chips && facts.Count > 0)
        {
            lines.Add(new ChipRow(
                facts,
                context.Regular,
                palette.Accent,
                palette.ChipFill,
                palette.Title));
        }

        if (!string.IsNullOrWhiteSpace(context.Options.Comment))
        {
            var comment = context.Options.Comment.Trim();
            if (comment.Length > 180)
            {
                comment = comment[..180].TrimEnd() + "…";
            }

            lines.Add(TextBlock.Fit(
                $"“{comment}”",
                context.Regular,
                spec.CommentMax,
                spec.CommentMax - 12f,
                3,
                palette.Muted,
                spec.MaxWidth - 60f,
                palette.TextShadow * 0.75f,
                0f));
        }

        return lines;
    }

    private static string BuildSubtitle(BaseItem item, PluginConfiguration config)
    {
        switch (item)
        {
            case Audio audio:
            {
                var artist = audio.Artists.FirstOrDefault() ?? audio.AlbumArtists.FirstOrDefault();
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(artist))
                {
                    parts.Add(artist);
                }

                if (!string.IsNullOrEmpty(audio.Album))
                {
                    parts.Add(audio.Album);
                }

                return string.Join("  ·  ", parts);
            }

            case MusicAlbum album:
            {
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(album.AlbumArtist))
                {
                    parts.Add(album.AlbumArtist);
                }

                if (config.ShowYear && album.ProductionYear.HasValue)
                {
                    parts.Add(album.ProductionYear.Value.ToString(CultureInfo.InvariantCulture));
                }

                return string.Join("  ·  ", parts);
            }

            case Episode episode:
            {
                var label = new StringBuilder(episode.SeriesName ?? string.Empty);
                if (episode.ParentIndexNumber.HasValue && episode.IndexNumber.HasValue)
                {
                    if (label.Length > 0)
                    {
                        label.Append("  ·  ");
                    }

                    label.Append(CultureInfo.InvariantCulture, $"S{episode.ParentIndexNumber:D2}E{episode.IndexNumber:D2}");
                }

                return label.ToString();
            }

            default:
            {
                var parts = new List<string>();
                if (config.ShowYear && item.ProductionYear.HasValue)
                {
                    parts.Add(item.ProductionYear.Value.ToString(CultureInfo.InvariantCulture));
                }

                if (config.ShowGenres && item.Genres.Length > 0)
                {
                    parts.Add(string.Join(", ", item.Genres.Take(3)));
                }

                return string.Join("  ·  ", parts);
            }
        }
    }

    private static List<string> BuildFacts(BaseItem item, PluginConfiguration config)
    {
        var facts = new List<string>();

        if (config.ShowRating && item.CommunityRating.HasValue)
        {
            // Leading marker: ChipRow draws a vector star, since most system fonts
            // have no U+2605 glyph and would render a tofu box.
            facts.Add($"{ChipRow.StarMarker}{item.CommunityRating.Value.ToString("0.0", CultureInfo.InvariantCulture)}");
        }

        if (!string.IsNullOrEmpty(item.OfficialRating))
        {
            facts.Add(item.OfficialRating);
        }

        if (config.ShowRuntime && item.RunTimeTicks is > 0)
        {
            var span = TimeSpan.FromTicks(item.RunTimeTicks.Value);
            facts.Add(span.TotalHours >= 1
                ? $"{(int)span.TotalHours}h {span.Minutes}m"
                : $"{span.Minutes}m {span.Seconds}s");
        }

        // Genres already appear in the subtitle for movies/series; for music they don't.
        if (config.ShowGenres && item is Audio or MusicAlbum && item.Genres.Length > 0)
        {
            facts.Add(item.Genres[0]);
        }

        return facts.Take(4).ToList();
    }

    // ---------------------------------------------------------------- helpers

    private static float TotalHeight(IEnumerable<IStoryLine> lines) =>
        lines.Sum(l => l.Height + l.SpacingAfter);

    private static void Dispose(IEnumerable<IStoryLine> lines)
    {
        foreach (var line in lines)
        {
            line.Dispose();
        }
    }

    private static SKImage? ArtImageFor(SKBitmap? art, SKRect rect) =>
        art is not null && !rect.IsEmpty ? SKImage.FromBitmap(art) : null;

    private static SKRect MoveTo(SKRect rect, float top) =>
        new(rect.Left, top, rect.Right, top + rect.Height);

    /// <summary>
    /// Scales a centred panel down to <paramref name="allowed"/> height, keeping its
    /// aspect. Returns empty when there is too little room to be worth showing
    /// artwork at all — the text is the payload, so it wins.
    /// </summary>
    private static SKRect ShrinkToFit(SKRect rect, float allowed, float minimum)
    {
        if (allowed < minimum)
        {
            return SKRect.Empty;
        }

        var width = rect.Width * (allowed / rect.Height);
        return SKRect.Create(Card.CenterX - (width / 2f), rect.Top, width, allowed);
    }

    private static SKTypeface CreateTypeface(SKFontStyleWeight weight)
    {
        foreach (var family in PreferredFonts)
        {
            var typeface = SKTypeface.FromFamilyName(family, weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
            if (typeface is not null && string.Equals(typeface.FamilyName, family, StringComparison.OrdinalIgnoreCase))
            {
                return typeface;
            }

            typeface?.Dispose();
        }

        return SKTypeface.FromFamilyName(null, weight, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
               ?? SKTypeface.Default;
    }

    private static SKRect MeasureArtRect(SKBitmap art)
    {
        var aspect = art.Width / (float)art.Height;

        float w, h;
        if (aspect > 1.25f)
        {
            w = 900f;
            h = w / aspect;
        }
        else if (aspect > 0.85f)
        {
            w = h = 720f;
            if (aspect >= 1f)
            {
                h = w / aspect;
            }
            else
            {
                w = h * aspect;
            }
        }
        else
        {
            h = 880f;
            w = h * aspect;
        }

        return SKRect.Create(Card.CenterX - (w / 2f), 0, w, h);
    }

    private SKColor ResolveAccent(string configured, SKBitmap? art)
    {
        if (!string.IsNullOrWhiteSpace(configured) && SKColor.TryParse(configured, out var parsed))
        {
            return parsed;
        }

        if (art is not null)
        {
            try
            {
                return DominantColor(art);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "StoryShare: dominant colour extraction failed, using default accent");
            }
        }

        return new SKColor(0x00, 0xA4, 0xDC); // Jellyfin blue
    }

    /// <summary>
    /// Averages the most saturated, mid-bright pixels of a downscaled copy — this
    /// picks up the poster's signature colour instead of its average mud.
    /// </summary>
    private static SKColor DominantColor(SKBitmap art)
    {
        using var small = art.Resize(new SKImageInfo(32, 32), Card.Sampling);
        if (small is null)
        {
            return new SKColor(0x00, 0xA4, 0xDC);
        }

        double r = 0, g = 0, b = 0, weightSum = 0;
        for (var y = 0; y < small.Height; y++)
        {
            for (var x = 0; x < small.Width; x++)
            {
                var px = small.GetPixel(x, y);
                if (px.Alpha < 128)
                {
                    continue;
                }

                px.ToHsl(out _, out var s, out var l);
                if (l is < 12f or > 92f)
                {
                    continue;
                }

                var weight = Math.Pow(s / 100.0, 2) + 0.02;
                r += px.Red * weight;
                g += px.Green * weight;
                b += px.Blue * weight;
                weightSum += weight;
            }
        }

        if (weightSum <= 0)
        {
            return new SKColor(0x00, 0xA4, 0xDC);
        }

        var color = new SKColor((byte)(r / weightSum), (byte)(g / weightSum), (byte)(b / weightSum));
        color.ToHsl(out var hue, out var sat, out var light);

        return SKColor.FromHsl(hue, Math.Clamp(sat * 1.35f, 45f, 95f), Math.Clamp(light, 48f, 68f));
    }
}
