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

        // Poster's bed is a blurred photograph under a heavy scrim, so its type stays
        // light whatever the preset. Full bleed paints its scrim in the chosen colour
        // — that is the point of choosing one when a busy cover is fighting the words
        // — so a pale preset there has to flip the type dark like a flat theme does.
        var readsLight = (flat || theme == CardTheme.FullBleed) && background.IsLight;
        var palette = new Palette(accent, background, readsLight);

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
            CardTheme.Cassette => BuildCrate(context),
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
    /// The picture is the card, and the type is set against its lower left corner
    /// rather than centred across the middle of it — a poster rather than a caption.
    /// The block is stacked upward from a fixed line, so it sits in the same place
    /// whether the title takes one row or three.
    ///
    /// The artwork is drawn as an oversized art panel rather than as a background
    /// layer, which is what gives Full bleed the push in, the drift and the beat every
    /// other style has: all three hang off the art panel, and a style with no panel
    /// had none of them — every animation produced the same video.
    /// </summary>
    private CardScene BuildFullBleed(LayoutContext context)
    {
        // Past every edge by more than Float's furthest drift, so sliding the card
        // can never bring a black margin into view.
        const float Overscan = 40f;

        var window = new SKRect(-Overscan, -Overscan, Card.Width + Overscan, Card.Height + Overscan);
        var (art, spare) = ChooseBleedSource(context.Art, context.Backdrop, window);

        var textLayer = BuildBleedLayer(context);

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
    /// Everything printed over the picture, in one layer: the mark up in the top
    /// left, and the stack of type up the bottom left with the now-playing line
    /// along the very bottom, both on the same margin. Baked rather than drawn per
    /// frame, and drawn outside the tilt and the drift, so Float slides the picture
    /// underneath while the words stay exactly where they were set.
    /// </summary>
    private SKImage BuildBleedLayer(LayoutContext context)
    {
        const float Margin = 88f;
        const float MaxWidth = Card.Width - (Margin * 2f);
        const float RuleWidth = 150f;
        const float RuleHeight = 5f;
        const float FactHeight = 46f;

        var palette = context.Palette;
        var background = palette.Background;

        // A chosen preset cannot recolour the picture without spoiling the one thing
        // this style exists to show, so it colours the scrim the type sits on instead.
        var deep = background.IsAuto ? SKColors.Black : background.Bottom;

        using var surface = SKSurface.Create(
            new SKImageInfo(Card.Width, Card.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        // Measured before anything is drawn: the stack is laid out from the bottom up,
        // so every height has to be known before the first baseline can be placed.
        using var title = TextBlock.Fit(
            string.IsNullOrWhiteSpace(context.Item.Name) ? "Untitled" : context.Item.Name,
            context.Bold,
            98f,
            54f,
            3,
            palette.Title,
            MaxWidth,
            palette.TextShadow,
            0f,
            SKTextAlign.Left,
            Margin);

        var subtitleText = BuildSubtitle(context.Item, context.Config);
        using var subtitle = string.IsNullOrEmpty(subtitleText)
            ? null
            : TextBlock.Fit(
                subtitleText,
                context.Regular,
                38f,
                28f,
                2,
                palette.Subtitle,
                MaxWidth,
                palette.TextShadow,
                0f,
                SKTextAlign.Left,
                Margin);

        using var comment = BuildBleedComment(context, MaxWidth, Margin);

        var facts = BuildIconFacts(context.Item, context.Config);
        var factsHeight = facts.Count > 0 ? FactHeight : 0f;

        // Bottom up from the safe line, with only the now-playing rule under it. Each
        // gap is only spent if the thing above it exists, so a track with no rating
        // does not leave a hole where the fact row would have been.
        var cursor = Card.SafeBottom;

        var factsTop = cursor - factsHeight;
        cursor = factsHeight > 0f ? factsTop - 40f : cursor;

        var ruleY = cursor - RuleHeight;
        cursor = ruleY - 42f;

        var commentTop = cursor - (comment?.Height ?? 0f);
        cursor = comment is null ? cursor : commentTop - 34f;

        var subtitleTop = cursor - (subtitle?.Height ?? 0f);
        cursor = subtitle is null ? cursor : subtitleTop - 20f;

        var titleTop = cursor - title.Height;

        // The scrim goes down first and is anchored to the type: a title that wraps to
        // three lines has to be sitting on the dark part of the ramp, not above it.
        DrawBleedScrim(canvas, titleTop - 60f, deep, !background.IsAuto);

        DrawBleedBrand(canvas, context, BleedBrandTop);

        if (!string.IsNullOrWhiteSpace(context.FooterText))
        {
            DrawBleedNowPlaying(
                canvas,
                context.FooterText.ToUpperInvariant(),
                Margin,
                Card.FooterBaseline,
                MaxWidth,
                palette,
                context.Bold);
        }

        title.Draw(canvas, titleTop);
        subtitle?.Draw(canvas, subtitleTop);
        comment?.Draw(canvas, commentTop);

        using (var rule = new SKPaint { IsAntialias = true, Color = palette.Accent })
        {
            canvas.DrawRect(SKRect.Create(Margin, ruleY, RuleWidth, RuleHeight), rule);
        }

        if (facts.Count > 0)
        {
            DrawBleedFacts(canvas, facts, Margin, factsTop, FactHeight, palette, context.Regular);
        }

        return surface.Snapshot();
    }

    /// <summary>
    /// The soft shadow that lifts light type off a picture — and nothing at all when
    /// the chosen background has flipped the type dark, where a black shadow under
    /// black letters only reads as a smudge.
    /// </summary>
    private static SKImageFilter? BleedTextShadow(Palette palette, float radius) =>
        palette.TextShadow > 0f
            ? SKImageFilter.CreateDropShadow(0, 2f, radius, radius, new SKColor(0, 0, 0, 150))
            : null;

    /// <summary>The caption, set as the tagline it stands in for rather than as a quote.</summary>
    private static TextBlock? BuildBleedComment(LayoutContext context, float maxWidth, float margin)
    {
        if (string.IsNullOrWhiteSpace(context.Options.Comment))
        {
            return null;
        }

        var comment = context.Options.Comment.Trim();
        if (comment.Length > 180)
        {
            comment = comment[..180].TrimEnd() + "…";
        }

        return TextBlock.Fit(
            comment,
            context.Regular,
            40f,
            30f,
            3,
            context.Palette.Muted,
            maxWidth,
            context.Palette.TextShadow * 0.75f,
            0f,
            SKTextAlign.Left,
            margin);
    }

    /// <summary>
    /// The ramp the type is read against: down the left, where the words are, and up
    /// from the bottom, where most of them are. Two directions rather than one, because
    /// a purely vertical ramp has to be almost opaque before left-aligned type over a
    /// bright corner is safe, and that buries the picture.
    /// </summary>
    private static void DrawBleedScrim(SKCanvas canvas, float typeTop, SKColor deep, bool chosen)
    {
        var full = new SKRect(0, 0, Card.Width, Card.Height);

        var start = Math.Clamp(typeTop / Card.Height, 0.2f, 0.8f);

        // A colour picked by hand is picked for a reason — almost always a cover busy
        // enough to swallow the words — so it is laid on harder than the automatic one,
        // which is only ever trying to stay out of the picture's way.
        var mid = (byte)(chosen ? 205 : 170);
        var foot = (byte)(chosen ? 255 : 246);
        var side = (byte)(chosen ? 185 : 150);

        using (var bottom = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(0, Card.Height),
                new[] { deep.WithAlpha(0), deep.WithAlpha(mid), deep.WithAlpha(foot) },
                new[] { start, (start + 1f) / 2f, 1f },
                SKShaderTileMode.Clamp)
        })
        {
            canvas.DrawRect(full, bottom);
        }

        using (var left = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(Card.Width * 0.72f, 0),
                new[] { deep.WithAlpha(side), deep.WithAlpha(0) },
                null,
                SKShaderTileMode.Clamp)
        })
        {
            canvas.DrawRect(full, left);
        }

        // The mark at the top needs the same courtesy, and Instagram lays its own
        // chrome across that strip anyway.
        using var top = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(0, Card.Height * 0.28f),
                new[] { deep.WithAlpha(165), deep.WithAlpha(0) },
                null,
                SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(full, top);
    }

    private const float BleedLogoHeight = 150f;

    // Well short of the width the type gets. A lockup is mostly wordmark, so it has to
    // be allowed to run wider than it is tall — but a mark that reaches the far margin
    // competes with the picture instead of signing it.
    private const float BleedLogoMaxWidth = 560f;

    /// <summary>
    /// Where the mark sits: into the top left corner, but off the very edge. Flush to
    /// the top read as crowded against it rather than bled off it, and this strip is
    /// also where Instagram lays its own chrome, so the drop buys the mark a little
    /// clearance from that too. Still deliberately above the safe band every other
    /// element respects — the whole bottom of the card belongs to the type.
    /// </summary>
    private const float BleedBrandTop = 46f;

    /// <summary>
    /// Type cannot sit on the canvas edge the way a picture can — glyphs touching it
    /// read as clipped rather than as bled — so the wordmark keeps this much air on
    /// the left and only the logo goes truly flush to that edge.
    /// </summary>
    private const float BleedWordmarkInset = 30f;

    /// <summary>
    /// The mark up in the top left, with the whole bottom of the card left to the
    /// type. A configured logo is drawn on its own — a lockup already says whose server
    /// this is — and without one the wordmark is set as type instead, so the corner is
    /// never empty.
    /// </summary>
    private void DrawBleedBrand(
        SKCanvas canvas,
        LayoutContext context,
        float top)
    {
        var palette = context.Palette;

        using var logo = LoadBrandLogo(context.Config.BrandLogoPath);
        if (logo is not null)
        {
            var aspect = logo.Width / (float)logo.Height;
            var width = Math.Min(BleedLogoMaxWidth, BleedLogoHeight * aspect);
            var height = width / aspect;
            var rect = SKRect.Create(0f, top, width, height);

            using var image = SKImage.FromBitmap(logo);
            using var paint = new SKPaint
            {
                // A lockup is usually drawn for a dark ground, so it keeps its shadow
                // on a pale scrim too — it is what separates a white wordmark from
                // white paper.
                ImageFilter = SKImageFilter.CreateDropShadow(0, 3f, 10f, 10f, new SKColor(0, 0, 0, 150))
            };
            canvas.DrawImage(image, new SKRect(0, 0, logo.Width, logo.Height), rect, Card.Sampling, paint);
            return;
        }

        using var small = new SKFont(context.Regular, 28f) { Edging = SKFontEdging.SubpixelAntialias };
        using var large = new SKFont(context.Bold, 48f) { Edging = SKFontEdging.SubpixelAntialias };
        using var muted = new SKPaint { IsAntialias = true, Color = palette.Muted };
        using var strong = new SKPaint
        {
            IsAntialias = true,
            Color = palette.Title,
            ImageFilter = BleedTextShadow(palette, 8f)
        };

        // Vertically off `top` alone, which already carries the air the mark keeps from
        // the edge: adding the left inset on this axis too would push the wordmark half
        // again further down than the logo it stands in for.
        canvas.DrawText(
            "Shared with", BleedWordmarkInset, top + 26f, SKTextAlign.Left, small, muted);
        canvas.DrawText(
            "Story Share", BleedWordmarkInset, top + 80f, SKTextAlign.Left, large, strong);
    }

    /// <summary>
    /// A brand lockup to print on the card, from wherever the server keeps it. Any
    /// failure is silent and simply means no logo: a card that renders without one is
    /// a great deal better than a share that fails because a path was mistyped.
    /// </summary>
    private SKBitmap? LoadBrandLogo(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return File.Exists(path) ? SKBitmap.Decode(path) : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Story Share could not read the brand logo at {Path}", path);
            return null;
        }
    }

    /// <summary>
    /// The line along the very bottom: a play mark and a letterspaced label.
    ///
    /// The label is the footer text, and it is the only copy of it on the card.
    /// Printing "NOW PLAYING" over the title and "Now playing in &lt;server&gt;" under
    /// the fold was the same sentence set twice.
    /// </summary>
    private static void DrawBleedNowPlaying(
        SKCanvas canvas,
        string label,
        float margin,
        float baseline,
        float maxWidth,
        Palette palette,
        SKTypeface bold)
    {
        const float Mark = 30f;
        const float Tracking = 5f;
        const float Gap = 22f;

        using (var play = new SKPaint { IsAntialias = true, Color = palette.Accent })
        {
            using var path = new SKPath();
            var top = baseline - Mark + 4f;
            path.MoveTo(margin, top);
            path.LineTo(margin + (Mark * 0.88f), top + (Mark / 2f));
            path.LineTo(margin, top + Mark);
            path.Close();
            canvas.DrawPath(path, play);
        }

        var textX = margin + Mark + Gap;
        using var font = new SKFont(bold, 30f) { Edging = SKFontEdging.SubpixelAntialias };

        // The label is whatever the footer says, so it can be any length. Letterspacing
        // is part of the width, which is why this measures rather than trusting the font.
        while (font.Size > 18f
            && font.MeasureText(label) + (label.Length * Tracking) > maxWidth - (textX - margin))
        {
            font.Size -= 1f;
        }

        using var paint = new SKPaint { IsAntialias = true, Color = palette.Accent };
        DrawTracked(canvas, label, textX, baseline, Tracking, font, paint);
    }

    /// <summary>
    /// Text with letterspacing, which SKFont has no setting for. Drawn a character at
    /// a time because a label this small set solid reads as a word rather than as a
    /// label, and every other approach means shipping a second font.
    /// </summary>
    private static void DrawTracked(
        SKCanvas canvas,
        string text,
        float x,
        float baseline,
        float tracking,
        SKFont font,
        SKPaint paint)
    {
        foreach (var c in text)
        {
            var glyph = c.ToString();
            canvas.DrawText(glyph, x, baseline, SKTextAlign.Left, font, paint);
            x += font.MeasureText(glyph) + tracking;
        }
    }

    /// <summary>What the icons stand for. Pill is the odd one out: it has no icon,
    /// because a certificate is already a badge and drawing one beside it says the
    /// same thing twice.</summary>
    private enum FactIcon
    {
        Star,
        Clock,
        Calendar,
        Tag,
        Pill
    }

    private readonly record struct IconFact(FactIcon Icon, string Text);

    /// <summary>
    /// The same facts the chip styles print, typed so each can be given the icon that
    /// belongs to it. The year is left out on purpose where the subtitle already
    /// carries it, which for everything but music it does.
    /// </summary>
    private static List<IconFact> BuildIconFacts(BaseItem item, PluginConfiguration config)
    {
        var facts = new List<IconFact>();

        if (item is Audio or MusicAlbum && item.ProductionYear.HasValue)
        {
            facts.Add(new IconFact(FactIcon.Calendar, item.ProductionYear.Value.ToString(CultureInfo.InvariantCulture)));
        }

        if (config.ShowRating && item.CommunityRating.HasValue)
        {
            facts.Add(new IconFact(
                FactIcon.Star,
                item.CommunityRating.Value.ToString("0.0", CultureInfo.InvariantCulture)));
        }

        if (!string.IsNullOrEmpty(item.OfficialRating))
        {
            facts.Add(new IconFact(FactIcon.Pill, item.OfficialRating));
        }

        if (config.ShowRuntime && item.RunTimeTicks is > 0)
        {
            var span = TimeSpan.FromTicks(item.RunTimeTicks.Value);
            facts.Add(new IconFact(
                FactIcon.Clock,
                span.TotalHours >= 1
                    ? $"{(int)span.TotalHours}h {span.Minutes}m"
                    : $"{span.Minutes}m {span.Seconds}s"));
        }

        if (config.ShowGenres && item is Audio or MusicAlbum && item.Genres.Length > 0)
        {
            facts.Add(new IconFact(FactIcon.Tag, item.Genres[0]));
        }

        return facts.Take(4).ToList();
    }

    private static void DrawBleedFacts(
        SKCanvas canvas,
        IReadOnlyList<IconFact> facts,
        float x,
        float top,
        float height,
        Palette palette,
        SKTypeface regular)
    {
        const float IconSize = 30f;
        const float IconGap = 12f;
        const float ItemGap = 38f;

        using var font = new SKFont(regular, 30f) { Edging = SKFontEdging.SubpixelAntialias };
        using var text = new SKPaint
        {
            IsAntialias = true,
            Color = palette.Subtitle,
            ImageFilter = BleedTextShadow(palette, 6f)
        };
        using var ink = new SKPaint
        {
            IsAntialias = true,
            Color = palette.Accent,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2.6f,
            StrokeCap = SKStrokeCap.Round
        };
        using var fill = new SKPaint { IsAntialias = true, Color = palette.Accent };

        var middle = top + (height / 2f);
        var baseline = middle + 11f;

        foreach (var fact in facts)
        {
            if (fact.Icon == FactIcon.Pill)
            {
                var width = font.MeasureText(fact.Text) + 36f;
                var pill = SKRect.Create(x, top + 4f, width, height - 8f);

                using (var edge = new SKPaint
                {
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = 2.6f,
                    Color = palette.Accent
                })
                {
                    canvas.DrawRoundRect(new SKRoundRect(pill, (height - 8f) / 2f), edge);
                }

                canvas.DrawText(fact.Text, pill.MidX, baseline, SKTextAlign.Center, font, text);
                x = pill.Right + ItemGap;
                continue;
            }

            DrawFactIcon(canvas, fact.Icon, SKRect.Create(x, middle - (IconSize / 2f), IconSize, IconSize), ink, fill);
            var textX = x + IconSize + IconGap;
            canvas.DrawText(fact.Text, textX, baseline, SKTextAlign.Left, font, text);
            x = textX + font.MeasureText(fact.Text) + ItemGap;
        }
    }

    /// <summary>
    /// The little glyphs beside each fact, drawn rather than set: a system font that
    /// has a calendar or a clock is not something a Jellyfin server can be assumed to
    /// have, and a missing one renders as a tofu box.
    /// </summary>
    private static void DrawFactIcon(SKCanvas canvas, FactIcon icon, SKRect box, SKPaint stroke, SKPaint fill)
    {
        switch (icon)
        {
            case FactIcon.Star:
            {
                using var path = new SKPath();
                var cx = box.MidX;
                var cy = box.MidY;
                var outer = box.Width / 2f;
                var inner = outer * 0.46f;

                for (var i = 0; i < 10; i++)
                {
                    var radius = i % 2 == 0 ? outer : inner;
                    var angle = (-MathF.PI / 2f) + (i * MathF.PI / 5f);
                    var px = cx + (radius * MathF.Cos(angle));
                    var py = cy + (radius * MathF.Sin(angle));

                    if (i == 0)
                    {
                        path.MoveTo(px, py);
                    }
                    else
                    {
                        path.LineTo(px, py);
                    }
                }

                path.Close();
                canvas.DrawPath(path, fill);
                break;
            }

            case FactIcon.Clock:
            {
                var radius = (box.Width / 2f) - 1.5f;
                canvas.DrawCircle(box.MidX, box.MidY, radius, stroke);
                canvas.DrawLine(box.MidX, box.MidY, box.MidX, box.MidY - (radius * 0.52f), stroke);
                canvas.DrawLine(box.MidX, box.MidY, box.MidX + (radius * 0.42f), box.MidY + (radius * 0.24f), stroke);
                break;
            }

            case FactIcon.Calendar:
            {
                var body = SKRect.Create(box.Left + 1.5f, box.Top + 5f, box.Width - 3f, box.Height - 6.5f);
                canvas.DrawRoundRect(new SKRoundRect(body, 4f), stroke);
                canvas.DrawLine(body.Left, body.Top + 8f, body.Right, body.Top + 8f, stroke);
                canvas.DrawLine(box.Left + 8f, box.Top, box.Left + 8f, box.Top + 8f, stroke);
                canvas.DrawLine(box.Right - 8f, box.Top, box.Right - 8f, box.Top + 8f, stroke);
                break;
            }

            case FactIcon.Tag:
            {
                using var path = new SKPath();
                path.MoveTo(box.Left + 2f, box.Top + 4f);
                path.LineTo(box.MidX + 3f, box.Top + 4f);
                path.LineTo(box.Right - 2f, box.MidY);
                path.LineTo(box.MidX + 3f, box.Bottom - 4f);
                path.LineTo(box.Left + 2f, box.Bottom - 4f);
                path.Close();
                canvas.DrawPath(path, stroke);
                canvas.DrawCircle(box.Left + 8f, box.MidY, 2.6f, fill);
                break;
            }
        }
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

    // ---------------------------------------------------------------- crate

    /// <summary>
    /// The cover at the front of a crate, with sleeves receding behind it. This slot
    /// used to draw a cassette, and the tape was never going to win: a shell is a
    /// fixed landscape shape and the cover had to be squeezed onto a label inside it,
    /// which is how it ended up a twentieth of the card. Sleeves in a crate are
    /// whatever shape the covers are, so the picture stays whole and full size and
    /// the decoration is simply the pile behind it.
    ///
    /// Mechanically this is Stack: cover front and centre, the rest baked into a
    /// layer underneath. They read differently because Stack fans upward about the
    /// bottom edge while these step sideways, the way records do when you push them
    /// forward one at a time.
    /// </summary>
    private CardScene BuildCrate(LayoutContext context)
    {
        const float Gap = 84f;
        const float Corner = 22f;
        // The sleeves stand out to the left, so the cover gives up enough width to
        // leave them somewhere to be. A landscape cover is the tight case: it starts
        // at the full 900 and would otherwise push the last sleeve off the card.
        const float ArtScale = 0.88f;

        var lines = BuildTextBlock(context, context.Palette, SpecFor(CardTheme.Cassette));
        var textHeight = TotalHeight(lines);

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

        var crateLayer = artRect.IsEmpty ? null : BuildCrateLayer(context.Art!, artRect, context.Palette, Corner);
        var textLayer = BuildOverlayLayer(lines, cursorY, Footer(context), null);
        Dispose(lines);

        return new CardScene
        {
            Theme = CardTheme.Cassette,
            Palette = context.Palette,
            Art = context.Art,
            ArtImage = ArtImageFor(context.Art, artRect),
            DecorLayer = crateLayer,
            ShadowLayer = artRect.IsEmpty ? null : BuildShadowLayer(artRect, Corner),
            TextLayer = textLayer,
            ArtRect = artRect,
            ArtCorner = Corner
        };
    }

    /// <summary>
    /// The sleeves behind the front one, each a little smaller than the one in front
    /// of it and pushed further left, so what shows past the cover's edge is a run of
    /// receding spines rather than three copies of the same picture side by side.
    /// Baked: only the face-up cover animates.
    /// </summary>
    private static SKImage BuildCrateLayer(SKBitmap art, SKRect front, Palette palette, float corner)
    {
        // Farthest first, and dimmest. Both numbers are fractions of the cover's own
        // width, so the crate looks the same depth whether the cover came out 587
        // wide or 792.
        var crate = new[]
        {
            (Scale: 0.88f, Offset: 0.195f, Dim: (byte)175),
            (Scale: 0.94f, Offset: 0.105f, Dim: (byte)95)
        };

        using var surface = SKSurface.Create(
            new SKImageInfo(Card.Width, Card.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        using var image = SKImage.FromBitmap(art);

        foreach (var sleeve in crate)
        {
            var width = front.Width * sleeve.Scale;
            var height = front.Height * sleeve.Scale;
            var rect = SKRect.Create(
                front.MidX - (width / 2f) - (front.Width * sleeve.Offset),
                front.MidY - (height / 2f),
                width,
                height);

            using var rounded = new SKRoundRect(rect, corner);

            using (var shadow = new SKPaint
            {
                IsAntialias = true,
                Color = new SKColor(0, 0, 0, 180),
                ImageFilter = SKImageFilter.CreateDropShadowOnly(0, 14f, 20f, 20f, new SKColor(0, 0, 0, 170))
            })
            {
                canvas.DrawRoundRect(rounded, shadow);
            }

            canvas.Save();
            canvas.ClipRoundRect(rounded, antialias: true);
            canvas.DrawImage(image, Card.CoverSourceRect(art, rect), rect, Card.Sampling, null);

            using (var scrim = new SKPaint { Color = new SKColor(0, 0, 0, sleeve.Dim) })
            {
                canvas.DrawRect(rect, scrim);
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
        }

        return surface.Snapshot();
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
