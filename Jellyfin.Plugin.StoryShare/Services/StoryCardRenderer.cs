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
    private readonly ServerInfo _server;
    private readonly ILogger<StoryCardRenderer> _logger;

    public StoryCardRenderer(ArtworkProvider artwork, ServerInfo server, ILogger<StoryCardRenderer> logger)
    {
        _artwork = artwork;
        _server = server;
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

    /// <summary>
    /// Themes that paint a flat background instead of the item's own artwork.
    /// Stated as the exceptions, because only two styles put the item's own
    /// photograph behind the card and every style added since has been flat.
    /// </summary>
    private static bool IsFlat(CardTheme theme) =>
        theme is not (CardTheme.Poster or CardTheme.FullBleed);

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

        // Expanded here rather than in the footer drawing, so a per-render override
        // from the API gets the same placeholders the configured text does.
        var footerText = _server.Expand(options.FooterText ?? config.FooterText);
        var context = new LayoutContext(item, options, config, palette, art, backdrop, footerText, bold, regular);

        var scene = theme switch
        {
            CardTheme.FullBleed => BuildFullBleed(context),
            CardTheme.Polaroid => BuildPolaroid(context),
            CardTheme.Vinyl => BuildVinyl(context),
            CardTheme.Stack => BuildStack(context),
            CardTheme.Ticket => BuildTicket(context),
            CardTheme.Cassette => BuildCassette(context),
            CardTheme.Review => BuildReview(context),
            _ => BuildClassic(theme, context)
        };

        // One place rather than a line in every Build*, so a style added later gets
        // the cover fitting and the animation choice without having to know about them.
        scene.Prepare(options.Animation ?? config.Animation);
        return scene;
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

    // ---------------------------------------------------------------- poster / minimal

    private CardScene BuildClassic(CardTheme theme, LayoutContext context)
    {
        var spec = SpecFor(theme);
        var lines = BuildTextBlock(context, context.Palette, spec);
        var textHeight = TotalHeight(lines);

        var artRect = context.Art is not null ? MeasureArtRect(context.Art) : SKRect.Empty;

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

        var cursorY = Card.SafeTop + Math.Max(0f, (available - totalHeight) / 2f);

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
            : BuildBackgroundLayer(context.Backdrop ?? context.Art);

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

    // ---------------------------------------------------------------- full bleed

    /// <summary>
    /// The picture is the card. It is drawn as an oversized art panel rather than as
    /// a background layer, which is what finally gives Full bleed the push in, the
    /// drift and the beat every other style has: all three hang off the art panel,
    /// and a style with no panel had none of them — every animation produced the
    /// same video.
    /// </summary>
    private CardScene BuildFullBleed(LayoutContext context)
    {
        // Past every edge by more than Float's furthest drift, so sliding the card
        // can never bring a black margin into view.
        const float Overscan = 40f;

        var lines = BuildTextBlock(context, context.Palette, SpecFor(CardTheme.FullBleed));
        var textHeight = TotalHeight(lines);
        var textTop = context.ContentBottom() - textHeight;

        var window = new SKRect(-Overscan, -Overscan, Card.Width + Overscan, Card.Height + Overscan);
        var (art, spare) = ChooseBleedSource(context.Art, context.Backdrop, window);

        // A chosen preset cannot recolour the picture without spoiling the one thing
        // this style exists to show, so it colours the scrim the text sits on instead.
        var background = context.Palette.Background;
        var deep = background.IsAuto ? SKColors.Black : background.Bottom;

        var textLayer = BuildOverlayLayer(
            lines,
            textTop,
            Footer(context),
            canvas => DrawBleedScrim(canvas, textTop, deep));
        Dispose(lines);

        return new CardScene
        {
            Theme = CardTheme.FullBleed,
            Palette = context.Palette,
            Art = art,
            Backdrop = spare,
            ArtImage = ArtImageFor(art, window),
            TextLayer = textLayer,
            ArtRect = window,
            ArtCorner = 0f,
            ArtBorder = false,
            // Nothing behind it to cast a shadow onto, and a gloss crossing a picture
            // this size reads as a smear on the lens rather than light on a panel.
            Sweep = false,
            FillWindow = true
        };
    }

    /// <summary>
    /// Which of the two images to fill the card with, and the one left over so the
    /// scene still owns — and disposes — both.
    ///
    /// Whichever loses least to the crop: a 16:9 backdrop in a 9:16 window keeps
    /// under a third of its width, and a wide shot is wide because its subject is
    /// spread across it. A poster is nearer the card's own shape and was drawn to be
    /// looked at whole, so it usually wins.
    /// </summary>
    private static (SKBitmap? Art, SKBitmap? Spare) ChooseBleedSource(
        SKBitmap? art,
        SKBitmap? backdrop,
        SKRect window)
    {
        if (art is null)
        {
            return (backdrop, null);
        }

        if (backdrop is null)
        {
            return (art, null);
        }

        return Card.AspectMismatch(backdrop, window) < Card.AspectMismatch(art, window)
            ? (backdrop, art)
            : (art, backdrop);
    }

    /// <summary>
    /// The ramp the type is read against. Baked into the text layer rather than drawn
    /// per frame, so Float slides the picture underneath it and the words stay where
    /// they were put.
    /// </summary>
    private static void DrawBleedScrim(SKCanvas canvas, float textTop, SKColor deep)
    {
        var full = new SKRect(0, 0, Card.Width, Card.Height);

        // Anchored to the text rather than to the card, so a title that wraps to
        // three lines is still sitting on the dark part of the ramp.
        var start = Math.Clamp((textTop - 280f) / Card.Height, 0.24f, 0.82f);

        using (var bottom = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(0, Card.Height),
                new[] { deep.WithAlpha(0), deep.WithAlpha(160), deep.WithAlpha(242) },
                new[] { start, (start + 1f) / 2f, 1f },
                SKShaderTileMode.Clamp)
        })
        {
            canvas.DrawRect(full, bottom);
        }

        // A little weight at the top as well. Instagram lays its own chrome over that
        // strip, and a bright sky up there leaves it with nothing to sit on.
        using var top = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(0, Card.Height * 0.26f),
                new[] { deep.WithAlpha(120), deep.WithAlpha(0) },
                null,
                SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(full, top);
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
            DrawUnderArt = (canvas, _) => DrawPaper(canvas, paperRect, photoRect, stock),
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

    // ---------------------------------------------------------------- ticket

    /// <summary>
    /// A billboard printed at the head of the stub. The plate runs the ticket's full
    /// width from its very top edge, so the picture is the first thing on the card
    /// rather than a stamp inside a border — a cover set in a padded band was a sixth
    /// of the card, and half of that was margin.
    /// </summary>
    private CardScene BuildTicket(LayoutContext context)
    {
        const float TicketWidth = 900f;
        const float Pad = 44f;
        const float PlateGap = 40f;
        const float StubGap = 34f;
        const float StubHeight = 176f;
        const float NotchRadius = 26f;
        const float Corner = 18f;
        const float MinPlateHeight = 300f;
        // Enough that a ticket using every pixel of height still reads as printed
        // on the card rather than bleeding off it.
        const float Breath = 40f;

        var stock = PaperStock(context.Palette.Background);

        // Printed on the ticket, so contrast follows the stock rather than the
        // surround — the same reasoning as Polaroid's caption.
        var print = context.Palette with { LightText = ColorMath.IsLight(stock.Top) };
        var lines = BuildTextBlock(context, print, SpecFor(CardTheme.Ticket));
        var textHeight = TotalHeight(lines);

        var available = context.ContentBottom(90f) - Card.SafeTop;

        // Everything the ticket has to carry below the plate. Whatever is left after
        // it, the picture takes — the printing is a fixed cost and the billboard is
        // the point of the style.
        var printing = PlateGap + textHeight + StubGap + StubHeight;

        var plateHeight = context.Art is null
            ? 0f
            : Math.Clamp(available - Breath - printing, MinPlateHeight, TicketWidth * 1.05f);

        var height = (plateHeight > 0f ? plateHeight + PlateGap : Pad) + textHeight + StubGap + StubHeight;
        var top = Card.SafeTop + Math.Max(0f, (available - height) / 2f);
        var ticketRect = SKRect.Create(Card.CenterX - (TicketWidth / 2f), top, TicketWidth, height);

        var plateRect = plateHeight > 0f
            ? SKRect.Create(ticketRect.Left, ticketRect.Top, TicketWidth, plateHeight)
            : SKRect.Empty;

        // Rounded where it meets the stock's own corners, square where the printing
        // begins. One radius cannot say that, so the clip is built by hand and the
        // artwork is given it instead of a corner size.
        var plateClip = plateHeight > 0f ? TopRounded(plateRect, Corner) : null;

        var textTop = plateHeight > 0f ? plateRect.Bottom + PlateGap : top + Pad;
        var perforationY = ticketRect.Bottom - StubHeight;

        // Baked rather than redrawn per frame: unlike Polaroid this card is never
        // rotated, so its straight edges are still landing on whole pixels.
        var decorLayer = BuildTicketLayer(ticketRect, plateClip, perforationY, NotchRadius, Corner, stock);

        var stubFacts = string.Join("  ·  ", BuildFacts(context.Item, context.Config)
            .Select(fact => fact.Length > 0 && fact[0] == ChipRow.StarMarker ? fact[1..].TrimStart() : fact));

        var printLayer = BuildOverlayLayer(
            lines,
            textTop,
            null,
            canvas =>
            {
                DrawPlateSeam(canvas, plateRect);
                DrawStub(canvas, ticketRect, perforationY, stubFacts, print, context.Bold, context.Regular);
            });
        Dispose(lines);

        var textLayer = BuildOverlayLayer(Array.Empty<IStoryLine>(), 0f, Footer(context), null);

        return new CardScene
        {
            Theme = CardTheme.Ticket,
            Palette = context.Palette with { Background = Recede(context.Palette.Background), LightText = false },
            Art = context.Art,
            ArtImage = ArtImageFor(context.Art, plateRect),
            DecorLayer = decorLayer,
            TiltedOverlay = printLayer,
            TextLayer = textLayer,
            ArtRect = plateRect,
            ArtClip = plateClip,
            ArtCorner = Corner,
            ArtBorder = false,
            FillWindow = true,
            // A poster cropped to a billboard loses its top and bottom equally, and
            // the faces are almost always in the upper half.
            ArtBiasY = 0.38f
        };
    }

    /// <summary>A rect rounded along its top edge only.</summary>
    private static SKRoundRect TopRounded(SKRect rect, float corner)
    {
        var rounded = new SKRoundRect();
        rounded.SetRectRadii(rect, new[]
        {
            new SKPoint(corner, corner),
            new SKPoint(corner, corner),
            new SKPoint(0f, 0f),
            new SKPoint(0f, 0f)
        });

        return rounded;
    }

    /// <summary>
    /// The seam where the printing plate ends and the bare stock begins. Two images
    /// meeting with nothing between them read as one image with a colour change in
    /// it; a hairline of ink says the plate was printed onto the card.
    /// </summary>
    private static void DrawPlateSeam(SKCanvas canvas, SKRect plate)
    {
        if (plate.IsEmpty)
        {
            return;
        }

        using var seam = new SKPaint { Color = new SKColor(0, 0, 0, 70) };
        canvas.DrawRect(SKRect.Create(plate.Left, plate.Bottom - 1f, plate.Width, 3f), seam);
    }

    /// <summary>Shadow, ticket stock, the torn perforation and the image plate.</summary>
    private static SKImage BuildTicketLayer(
        SKRect ticket,
        SKRoundRect? plate,
        float perforationY,
        float notchRadius,
        float corner,
        PaperStockColors stock)
    {
        using var surface = SKSurface.Create(
            new SKImageInfo(Card.Width, Card.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        using var body = TicketPath(ticket, perforationY, notchRadius, corner);

        using (var shadow = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(0, 0, 0, 190),
            ImageFilter = SKImageFilter.CreateDropShadowOnly(0, 24f, 30f, 30f, new SKColor(0, 0, 0, 190))
        })
        {
            canvas.DrawPath(body, shadow);
        }

        using (var fill = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(ticket.Left, ticket.Top),
                new SKPoint(ticket.Right, ticket.Bottom),
                new[] { stock.Top, stock.Bottom },
                null,
                SKShaderTileMode.Clamp)
        })
        {
            canvas.DrawPath(body, fill);
        }

        using (var edge = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
            IsAntialias = true,
            Color = stock.Edge
        })
        {
            canvas.DrawPath(body, edge);
        }

        // The tear line. It stops short of the notches so the dashes do not run out
        // into the bites taken from the edges.
        using (var perforation = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3f,
            IsAntialias = true,
            Color = stock.Edge,
            PathEffect = SKPathEffect.CreateDash(new[] { 12f, 12f }, 0f)
        })
        {
            canvas.DrawLine(
                ticket.Left + notchRadius + 12f,
                perforationY,
                ticket.Right - notchRadius - 12f,
                perforationY,
                perforation);
        }

        // Under the picture, so a plate the artwork somehow fails to cover still
        // reads as a printed panel rather than as a hole in the ticket.
        if (plate is not null)
        {
            using var ink = new SKPaint { IsAntialias = true, Color = new SKColor(0x14, 0x15, 0x18) };
            canvas.DrawRoundRect(plate, ink);
        }

        return surface.Snapshot();
    }

    /// <summary>The ticket outline, with a semicircle bitten out of each edge at the tear.</summary>
    private static SKPath TicketPath(SKRect ticket, float perforationY, float notchRadius, float corner)
    {
        var body = new SKPath();
        body.AddRoundRect(new SKRoundRect(ticket, corner));

        using var notches = new SKPath();
        notches.AddCircle(ticket.Left, perforationY, notchRadius);
        notches.AddCircle(ticket.Right, perforationY, notchRadius);

        // Op returns null if Skia cannot resolve the operation; the un-notched
        // outline is a perfectly serviceable ticket, so fall back to it.
        var cut = body.Op(notches, SKPathOp.Difference);
        if (cut is null)
        {
            return body;
        }

        body.Dispose();
        return cut;
    }

    private static void DrawStub(
        SKCanvas canvas,
        SKRect ticket,
        float perforationY,
        string facts,
        Palette palette,
        SKTypeface bold,
        SKTypeface regular)
    {
        // Letterspaced by hand: SKFont exposes no tracking, and "ADMIT ONE" set
        // solid does not read as ticket printing.
        using (var font = new SKFont(bold, 34f) { Edging = SKFontEdging.SubpixelAntialias })
        using (var paint = new SKPaint { IsAntialias = true, Color = palette.Title })
        {
            canvas.DrawText("A D M I T   O N E", Card.CenterX, perforationY + 62f, SKTextAlign.Center, font, paint);
        }

        if (!string.IsNullOrEmpty(facts))
        {
            using var font = new SKFont(regular, 26f) { Edging = SKFontEdging.SubpixelAntialias };
            using var paint = new SKPaint { IsAntialias = true, Color = palette.Muted };
            canvas.DrawText(facts, Card.CenterX, perforationY + 104f, SKTextAlign.Center, font, paint);
        }

        DrawBarcode(
            canvas,
            SKRect.Create(Card.CenterX - 210f, perforationY + 122f, 420f, 38f),
            palette.Title.WithAlpha(210),
            StableHash(facts));
    }

    /// <summary>
    /// Decorative bars. Seeded from a stable hash rather than <c>string.GetHashCode</c>,
    /// which is randomised per process — the same card has to come out identical on
    /// every render, or a cached copy and a fresh one would not match.
    ///
    /// Laid out first and drawn second, so the run can be centred under the printing
    /// above it: bars are whatever width the seed says, so the last one almost never
    /// lands on the right-hand edge, and filling from the left left the whole code
    /// sitting off-centre.
    /// </summary>
    private static void DrawBarcode(SKCanvas canvas, SKRect area, SKColor color, uint seed)
    {
        var bars = new List<(float Width, float Gap)>();
        var state = seed | 1u;
        var used = 0f;

        while (true)
        {
            state = (state * 1664525u) + 1013904223u;
            var width = 3f + ((state >> 16) % 5);
            var gap = 3f + ((state >> 8) % 4);

            if (used + width > area.Width)
            {
                break;
            }

            bars.Add((width, gap));
            used += width + gap;
        }

        if (bars.Count == 0)
        {
            return;
        }

        // The trailing gap is not part of the code, so it is not part of the width
        // being centred either.
        used -= bars[^1].Gap;

        using var paint = new SKPaint { Color = color, IsAntialias = true };
        var x = area.MidX - (used / 2f);

        foreach (var bar in bars)
        {
            canvas.DrawRect(SKRect.Create(x, area.Top, bar.Width, area.Height), paint);
            x += bar.Width + bar.Gap;
        }
    }

    private static uint StableHash(string? value)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in value ?? string.Empty)
            {
                hash ^= c;
                hash *= 16777619u;
            }

            return hash;
        }
    }

    // ---------------------------------------------------------------- cassette

    /// <summary>
    /// The cover is the sleeve, and the tape is halfway out of it. Printing the cover
    /// on a shell's label meant a fixed landscape aspect capped it at a twentieth of
    /// the card; a sleeve is whatever shape the thing inside it is, so the cover is
    /// printed at its own proportions and at the size a sleeve would really be.
    /// </summary>
    private CardScene BuildCassette(LayoutContext context)
    {
        const float MaxSleeveWidth = 620f;
        const float MinSleeveHeight = 280f;
        const float Gap = 76f;
        const float Corner = 10f;
        // A C-cassette shell is 100x64mm, and looking wrong here is immediately
        // obvious to anyone who has held one.
        const float ShellAspect = 1.5625f;
        const float ShellWidthRatio = 0.90f;
        const float ShellHeightRatio = 0.86f;
        // How far out the tape sits at rest, and how much further it slides across
        // the loop, both as a fraction of the shell's own width. The still is drawn
        // at phase 0, so a tape that started fully inside would leave the style's
        // whole idea out of every shared image.
        const float PeekRatio = 0.40f;
        const float TravelRatio = 0.14f;
        const float FallbackAspect = 0.72f;

        var lines = BuildTextBlock(context, context.Palette, SpecFor(CardTheme.Cassette));
        var textHeight = TotalHeight(lines);

        var available = context.ContentBottom() - Card.SafeTop;
        var budget = available - Gap - textHeight;

        var aspect = context.Art is null
            ? FallbackAspect
            : context.Art.Width / (float)context.Art.Height;

        var sleeveWidth = MaxSleeveWidth;
        var sleeveHeight = sleeveWidth / aspect;
        if (sleeveHeight > budget)
        {
            sleeveHeight = Math.Max(budget, MinSleeveHeight);
            sleeveWidth = sleeveHeight * aspect;
        }

        // Narrow enough to hide behind the sleeve at rest, in both directions: a
        // landscape cover makes for a short sleeve, and a shell taller than the thing
        // it is supposed to be inside gives the game away.
        var shellWidth = MathF.Min(sleeveWidth * ShellWidthRatio, sleeveHeight * ShellHeightRatio * ShellAspect);
        var shellHeight = shellWidth / ShellAspect;

        // Whole pixels: the shell is a baked layer translated on the phase, and a
        // fractional offset resamples it every frame.
        var peek = MathF.Round(shellWidth * PeekRatio);
        var travel = MathF.Round(shellWidth * TravelRatio);

        var objectHeight = context.Art is null ? shellHeight : sleeveHeight;
        var top = MathF.Round(Card.SafeTop + Math.Max(0f, (available - (objectHeight + Gap + textHeight)) / 2f));

        // Centred as a composition rather than as a rectangle. The tape is already
        // out from behind the sleeve in the still, so centring the sleeve on its own
        // would leave the pair sitting to the right of everything below it.
        var span = ((sleeveWidth + shellWidth) / 2f) + peek;
        var left = MathF.Round(Card.CenterX - (span / 2f));

        var sleeveRect = context.Art is null
            ? SKRect.Empty
            : SKRect.Create(left, top, sleeveWidth, sleeveHeight);

        // Nothing to sleeve: the tape is the whole card, centred and still.
        var shellRect = context.Art is null
            ? SKRect.Create(MathF.Round(Card.CenterX - (shellWidth / 2f)), top, shellWidth, shellHeight)
            : SKRect.Create(
                MathF.Round(left + ((sleeveWidth - shellWidth) / 2f)),
                MathF.Round(top + ((sleeveHeight - shellHeight) / 2f)),
                shellWidth,
                shellHeight);

        var rest = context.Art is null ? 0f : peek;
        var slide = context.Art is null ? 0f : travel;

        var labelRect = SKRect.Create(
            shellRect.Left + (shellWidth * 0.08f),
            shellRect.Top + (shellHeight * 0.10f),
            shellWidth * 0.84f,
            shellHeight * 0.30f);

        var windowRect = SKRect.Create(
            shellRect.Left + (shellWidth * 0.18f),
            shellRect.Top + (shellHeight * 0.52f),
            shellWidth * 0.64f,
            shellHeight * 0.28f);

        var hubRadius = windowRect.Height * 0.32f;
        var leftHub = new SKPoint(windowRect.Left + (windowRect.Width * 0.24f), windowRect.MidY);
        var rightHub = new SKPoint(windowRect.Left + (windowRect.Width * 0.76f), windowRect.MidY);

        // Baked into a layer barely bigger than itself, because it is drawn at a
        // different place every frame and its drop shadow is far too expensive to
        // blur again each time. The hubs are the one part left as vectors: they turn.
        var shellLayer = BuildShellLayer(shellRect, labelRect, windowRect, leftHub, rightHub, hubRadius, context.Palette);

        var textLayer = BuildOverlayLayer(lines, top + objectHeight + Gap, Footer(context), null);
        Dispose(lines);

        return new CardScene
        {
            Theme = CardTheme.Cassette,
            Palette = context.Palette,
            Art = context.Art,
            ArtImage = ArtImageFor(context.Art, sleeveRect),
            ShadowLayer = sleeveRect.IsEmpty ? null : BuildShadowLayer(sleeveRect, Corner),
            MovingLayers = new[] { shellLayer },
            // Under the artwork, because the tape comes out from behind the sleeve —
            // and the sleeve's own shadow, drawn with the art panel, then falls across
            // the part of the shell that is out in the open.
            DrawUnderArt = (canvas, phase) =>
            {
                var offset = rest + MathF.Round(slide * (1f - MathF.Cos(2f * MathF.PI * phase)) / 2f);

                canvas.Save();
                canvas.Translate(offset, 0f);
                canvas.DrawImage(shellLayer, shellRect.Left - Card.ShadowPad, shellRect.Top - Card.ShadowPad);
                DrawHubs(canvas, leftHub, rightHub, hubRadius, phase, context.Palette);
                canvas.Restore();
            },
            DrawOverArt = sleeveRect.IsEmpty
                ? null
                : (canvas, _) => DrawSleeve(canvas, sleeveRect, Corner, context.Palette),
            TextLayer = textLayer,
            ArtRect = sleeveRect,
            ArtCorner = Corner,
            ArtBorder = false
        };
    }

    /// <summary>
    /// The sleeve's own edges, printed over the cover: a folded spine down the left
    /// and the open mouth on the right, which is the edge the tape comes out of.
    /// Without them the cover is a picture with a cassette lying behind it.
    /// </summary>
    private static void DrawSleeve(SKCanvas canvas, SKRect sleeve, float corner, Palette palette)
    {
        const float Spine = 22f;
        const float Mouth = 34f;

        using var rounded = new SKRoundRect(sleeve, corner);

        canvas.Save();
        canvas.ClipRoundRect(rounded, antialias: true);

        // The fold is dark in its crease and catches the light coming off it.
        using (var crease = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(sleeve.Left, 0f),
                new SKPoint(sleeve.Left + Spine, 0f),
                new[]
                {
                    new SKColor(0, 0, 0, 150),
                    new SKColor(0, 0, 0, 30),
                    new SKColor(255, 255, 255, 26)
                },
                new[] { 0f, 0.72f, 1f },
                SKShaderTileMode.Clamp)
        })
        {
            canvas.DrawRect(SKRect.Create(sleeve.Left, sleeve.Top, Spine, sleeve.Height), crease);
        }

        // A shadow inside the opening, so the tape reads as coming out of the sleeve
        // rather than sliding along behind it.
        using (var mouth = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(sleeve.Right - Mouth, 0f),
                new SKPoint(sleeve.Right, 0f),
                new[] { new SKColor(0, 0, 0, 0), new SKColor(0, 0, 0, 130) },
                null,
                SKShaderTileMode.Clamp)
        })
        {
            canvas.DrawRect(SKRect.Create(sleeve.Right - Mouth, sleeve.Top, Mouth, sleeve.Height), mouth);
        }

        canvas.Restore();

        using var edge = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
            IsAntialias = true,
            Color = palette.Accent.WithAlpha(90)
        };
        canvas.DrawRoundRect(rounded, edge);
    }

    /// <summary>
    /// The tape itself: shell, blank label, window and the wound packs behind it,
    /// baked at its own size rather than across the card, because it moves.
    /// Everything is laid out in card coordinates and the layer is offset to suit,
    /// so the caller can go on thinking in the same numbers it used to place it.
    /// </summary>
    private static SKImage BuildShellLayer(
        SKRect body,
        SKRect label,
        SKRect window,
        SKPoint leftHub,
        SKPoint rightHub,
        float hubRadius,
        Palette palette)
    {
        const float Corner = 14f;

        var width = (int)MathF.Ceiling(body.Width + (Card.ShadowPad * 2f));
        var height = (int)MathF.Ceiling(body.Height + (Card.ShadowPad * 2f));

        using var surface = SKSurface.Create(
            new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(Card.ShadowPad - body.Left, Card.ShadowPad - body.Top);

        using var shell = new SKRoundRect(body, Corner);

        using (var shadow = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(0, 0, 0, 190),
            ImageFilter = SKImageFilter.CreateDropShadowOnly(0, 24f, 32f, 32f, new SKColor(0, 0, 0, 190))
        })
        {
            canvas.DrawRoundRect(shell, shadow);
        }

        // Smoked plastic, tinted towards the accent so the shell picks up the
        // artwork's colour the way every other style does.
        using (var plastic = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(body.Left, body.Top),
                new SKPoint(body.Right, body.Bottom),
                new[]
                {
                    ColorMath.Lerp(new SKColor(0x2A, 0x2C, 0x33), palette.Accent, 0.22f),
                    new SKColor(0x14, 0x15, 0x1A)
                },
                null,
                SKShaderTileMode.Clamp)
        })
        {
            canvas.DrawRoundRect(shell, plastic);
        }

        using (var edge = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
            IsAntialias = true,
            Color = new SKColor(255, 255, 255, 40)
        })
        {
            canvas.DrawRoundRect(shell, edge);
        }

        // The label is left blank and ruled. Nothing is printed on it any more — the
        // cover is on the sleeve, where it is four times the size — and ruled paper is
        // what a mixtape's label looks like before anybody has written on it.
        using (var paper = new SKPaint { IsAntialias = true, Color = new SKColor(0xE9, 0xE4, 0xD8) })
        {
            using var sticker = new SKRoundRect(label, 6f);
            canvas.DrawRoundRect(sticker, paper);
        }

        DrawLabelRules(canvas, label);

        using var pane = new SKRoundRect(window, 12f);
        using (var windowPaint = new SKPaint { IsAntialias = true, Color = new SKColor(0x0A, 0x0B, 0x0E) })
        {
            canvas.DrawRoundRect(pane, windowPaint);
        }

        // The wound tape either side, clipped to the window — a pack wider than the
        // opening is exactly what you see through a real shell. Static, because a
        // reel of tape looks the same at any angle; only the hubs need to move.
        canvas.Save();
        canvas.ClipRoundRect(pane, antialias: true);

        using (var tape = new SKPaint { IsAntialias = true, Color = new SKColor(0x3A, 0x2A, 0x22) })
        {
            canvas.DrawCircle(leftHub, hubRadius * 1.9f, tape);
            canvas.DrawCircle(rightHub, hubRadius * 1.9f, tape);
        }

        canvas.Restore();

        using (var screw = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 34) })
        {
            var inset = 26f;
            canvas.DrawCircle(body.Left + inset, body.Top + inset, 7f, screw);
            canvas.DrawCircle(body.Right - inset, body.Top + inset, 7f, screw);
            canvas.DrawCircle(body.Left + inset, body.Bottom - inset, 7f, screw);
            canvas.DrawCircle(body.Right - inset, body.Bottom - inset, 7f, screw);
        }

        return surface.Snapshot();
    }

    /// <summary>
    /// The lines a mixtape's track list gets written on. Blank paper on the shell
    /// looks like something failed to draw; ruled paper looks like a cassette nobody
    /// has filled in yet.
    /// </summary>
    private static void DrawLabelRules(SKCanvas canvas, SKRect label)
    {
        const int Rules = 3;
        const float Inset = 18f;

        using var rule = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
            IsAntialias = true,
            Color = new SKColor(0x8C, 0x86, 0x78, 120)
        };

        var span = label.Height * 0.62f;
        var step = span / (Rules - 1);
        var first = label.MidY - (span / 2f);

        for (var i = 0; i < Rules; i++)
        {
            var y = first + (i * step);
            canvas.DrawLine(label.Left + Inset, y, label.Right - Inset, y, rule);
        }
    }

    /// <summary>The two toothed hubs, turning a whole revolution across the loop.</summary>
    private static void DrawHubs(
        SKCanvas canvas,
        SKPoint left,
        SKPoint right,
        float radius,
        float phase,
        Palette palette)
    {
        const int Teeth = 6;

        using var hub = new SKPaint { IsAntialias = true, Color = new SKColor(0xD8, 0xD8, 0xDC) };
        using var tooth = new SKPaint { IsAntialias = true, Color = new SKColor(0x1A, 0x1B, 0x20) };
        using var rim = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3f,
            IsAntialias = true,
            Color = palette.Accent.WithAlpha(150)
        };

        foreach (var center in new[] { left, right })
        {
            canvas.Save();
            canvas.RotateDegrees(360f * phase, center.X, center.Y);

            canvas.DrawCircle(center, radius, hub);

            for (var i = 0; i < Teeth; i++)
            {
                var angle = i * 2f * MathF.PI / Teeth;
                canvas.DrawCircle(
                    center.X + (radius * 0.62f * MathF.Cos(angle)),
                    center.Y + (radius * 0.62f * MathF.Sin(angle)),
                    radius * 0.20f,
                    tooth);
            }

            canvas.DrawCircle(center, radius, rim);
            canvas.Restore();
        }
    }

    // ---------------------------------------------------------------- review

    private CardScene BuildReview(LayoutContext context)
    {
        const float Gap = 66f;
        const float FrameInset = 58f;
        const float ArtScale = 0.72f;

        var lines = BuildTextBlock(context, context.Palette, SpecFor(CardTheme.Review));
        var textHeight = TotalHeight(lines);

        // Smaller than every other style's cover on purpose: here the poster is
        // the illustration and the words are the point.
        var artRect = SKRect.Empty;
        if (context.Art is not null)
        {
            var full = MeasureArtRect(context.Art);
            var width = full.Width * ArtScale;
            artRect = SKRect.Create(Card.CenterX - (width / 2f), 0, width, full.Height * ArtScale);
        }

        var available = context.ContentBottom() - Card.SafeTop;
        var gap = artRect.IsEmpty ? 0f : Gap;
        var totalHeight = artRect.Height + gap + textHeight;

        if (!artRect.IsEmpty && totalHeight > available)
        {
            artRect = ShrinkToFit(artRect, available - gap - textHeight, 200f);
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

        // The frame goes in the topmost layer, so it reads as printing on the card
        // rather than something the poster could sit on top of.
        var textLayer = BuildOverlayLayer(
            lines,
            cursorY,
            Footer(context),
            canvas => DrawReviewFrame(canvas, FrameInset, context.Palette));
        Dispose(lines);

        return new CardScene
        {
            Theme = CardTheme.Review,
            Palette = context.Palette,
            Art = context.Art,
            ArtImage = ArtImageFor(context.Art, artRect),
            ShadowLayer = artRect.IsEmpty ? null : BuildShadowLayer(artRect, 16f),
            TextLayer = textLayer,
            ArtRect = artRect,
            ArtCorner = 16f
        };
    }

    /// <summary>A hairline border with a tick at each corner, like a printed plate.</summary>
    private static void DrawReviewFrame(SKCanvas canvas, float inset, Palette palette)
    {
        const float Tick = 34f;

        var frame = new SKRect(inset, inset, Card.Width - inset, Card.Height - inset);

        using (var line = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
            IsAntialias = true,
            Color = palette.Title.WithAlpha(60)
        })
        {
            canvas.DrawRect(frame, line);
        }

        using var corner = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 5f,
            IsAntialias = true,
            Color = palette.Accent.WithAlpha(210)
        };

        foreach (var (x, y, dx, dy) in new[]
        {
            (frame.Left, frame.Top, 1f, 1f),
            (frame.Right, frame.Top, -1f, 1f),
            (frame.Left, frame.Bottom, 1f, -1f),
            (frame.Right, frame.Bottom, -1f, -1f)
        })
        {
            canvas.DrawLine(x, y, x + (Tick * dx), y, corner);
            canvas.DrawLine(x, y, x, y + (Tick * dy), corner);
        }
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

    /// <summary>The blurred, oversized bed a card sits on. Poster's, and nobody else's.</summary>
    private static SKImage? BuildBackgroundLayer(SKBitmap? source)
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
        using (var paint = new SKPaint
        {
            ImageFilter = SKImageFilter.CreateBlur(48f, 48f, SKShaderTileMode.Clamp)
        })
        {
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
        // Printed inside the ticket, so everything is a size down and the text can
        // never be wider than the stock it sits on.
        CardTheme.Ticket => new TextSpec
        {
            TitleMax = 56f,
            TitleMin = 32f,
            TitleLines = 2,
            TitleSpacing = 16f,
            SubtitleMax = 32f,
            SubtitleMin = 24f,
            CommentMax = 30f,
            CommentLines = 2,
            MaxWidth = 660f
        },
        CardTheme.Cassette => new TextSpec
        {
            TitleMax = 64f,
            TitleMin = 38f,
            TitleLines = 2,
            SubtitleMax = 36f,
            CommentMax = 32f,
            MaxWidth = 840f
        },
        // The caption is the review, so it gets body-copy size and no quotes, and
        // the score is a star row rather than one more chip.
        CardTheme.Review => new TextSpec
        {
            TitleMax = 68f,
            TitleMin = 40f,
            TitleLines = 2,
            TitleSpacing = 16f,
            SubtitleMax = 34f,
            SubtitleMin = 26f,
            CommentMax = 36f,
            CommentLines = 4,
            QuoteComment = false,
            MaxWidth = 760f,
            Chips = false,
            Stars = true
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

        // The star row and the chip row occupy the same slot: a Review card that
        // showed both would state the same score twice.
        if (spec.Stars)
        {
            if (context.Config.ShowRating && context.Item.CommunityRating is > 0)
            {
                // Jellyfin scores out of ten; a star row is out of five.
                lines.Add(new StarRating(
                    context.Item.CommunityRating.Value / 2f,
                    32f,
                    palette.Muted.WithAlpha(70)));
            }
        }
        else if (spec.Chips)
        {
            var facts = BuildFacts(context.Item, context.Config);
            if (facts.Count > 0)
            {
                lines.Add(new ChipRow(
                    facts,
                    context.Regular,
                    palette.Accent,
                    palette.ChipFill,
                    palette.Title));
            }
        }

        if (!string.IsNullOrWhiteSpace(context.Options.Comment))
        {
            var comment = context.Options.Comment.Trim();
            if (comment.Length > 180)
            {
                comment = comment[..180].TrimEnd() + "…";
            }

            lines.Add(TextBlock.Fit(
                spec.QuoteComment ? $"“{comment}”" : comment,
                context.Regular,
                spec.CommentMax,
                spec.CommentMax - 12f,
                spec.CommentLines,
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
