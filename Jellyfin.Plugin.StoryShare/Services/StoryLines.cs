using System.Text;
using Jellyfin.Plugin.StoryShare.Configuration;
using SkiaSharp;

namespace Jellyfin.Plugin.StoryShare.Services;

/// <summary>
/// The colours one card is drawn with. <see cref="LightText"/> flips the type to
/// dark, which a pale background preset needs and a photographic one never does —
/// Poster and Full bleed always put their text over a dark scrim.
/// </summary>
internal sealed record Palette(SKColor Accent, CardBackground Background, bool LightText)
{
    public SKColor Title => LightText ? new SKColor(20, 23, 28) : SKColors.White;

    public SKColor Subtitle => LightText ? new SKColor(64, 71, 82) : new SKColor(226, 232, 240);

    public SKColor Muted => LightText ? new SKColor(104, 112, 124) : new SKColor(203, 213, 225);

    public SKColor ChipFill => LightText ? new SKColor(0, 0, 0, 20) : new SKColor(255, 255, 255, 34);

    public SKColor Footer => LightText ? new SKColor(52, 58, 68, 235) : new SKColor(226, 232, 240, 220);

    /// <summary>Drop-shadow radius for text. Pointless on a pale background.</summary>
    public float TextShadow => LightText ? 0f : 8f;
}

/// <summary>Per-theme typography. The defaults are the Poster/Full bleed/Minimal look.</summary>
internal sealed class TextSpec
{
    public float TitleMax { get; init; } = 78f;

    public float TitleMin { get; init; } = 46f;

    public int TitleLines { get; init; } = 3;

    public float TitleSpacing { get; init; } = 22f;

    public float SubtitleMax { get; init; } = 40f;

    public float SubtitleMin { get; init; } = 28f;

    public float CommentMax { get; init; } = 38f;

    public float MaxWidth { get; init; } = Card.TextMaxWidth;

    public bool Chips { get; init; } = true;
}

internal interface IStoryLine : IDisposable
{
    float Height { get; }

    float SpacingAfter { get; }

    void Draw(SKCanvas canvas, float top);
}

/// <summary>One or more lines of text at a size chosen to fit.</summary>
internal sealed class TextBlock : IStoryLine
{
    private readonly IReadOnlyList<string> _lines;
    private readonly SKFont _font;
    private readonly SKPaint _paint;
    private readonly float _lineHeight;

    private TextBlock(
        IReadOnlyList<string> lines,
        SKFont font,
        SKPaint paint,
        float lineHeight,
        float spacingAfter)
    {
        _lines = lines;
        _font = font;
        _paint = paint;
        _lineHeight = lineHeight;
        SpacingAfter = spacingAfter;
    }

    public float Height => _lines.Count * _lineHeight;

    public float SpacingAfter { get; }

    public static TextBlock Fit(
        string text,
        SKTypeface typeface,
        float maxSize,
        float minSize,
        int maxLines,
        SKColor color,
        float maxWidth,
        float shadow,
        float spacingAfter)
    {
        var font = new SKFont(typeface, maxSize) { Edging = SKFontEdging.SubpixelAntialias };
        var paint = new SKPaint
        {
            IsAntialias = true,
            Color = color,
            ImageFilter = shadow > 0
                ? SKImageFilter.CreateDropShadow(0, 3f, shadow, shadow, new SKColor(0, 0, 0, 170))
                : null
        };

        List<string> lines;
        while (true)
        {
            lines = Wrap(text, font, maxWidth);
            if (lines.Count <= maxLines || font.Size <= minSize)
            {
                break;
            }

            font.Size -= 2f;
        }

        if (lines.Count > maxLines)
        {
            lines = lines.Take(maxLines).ToList();
            lines[^1] = lines[^1].TrimEnd() + "…";
        }

        var metrics = font.Metrics;
        var lineHeight = (metrics.Descent - metrics.Ascent) * 1.02f;
        return new TextBlock(lines, font, paint, lineHeight, spacingAfter);
    }

    public void Draw(SKCanvas canvas, float top)
    {
        var baseline = top - _font.Metrics.Ascent;

        foreach (var line in _lines)
        {
            canvas.DrawText(line, Card.CenterX, baseline, SKTextAlign.Center, _font, _paint);
            baseline += _lineHeight;
        }
    }

    public void Dispose()
    {
        _font.Dispose();
        _paint.Dispose();
    }

    private static List<string> Wrap(string text, SKFont font, float maxWidth)
    {
        var result = new List<string>();
        var current = new StringBuilder();

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = current.Length == 0 ? word : $"{current} {word}";
            if (font.MeasureText(candidate) <= maxWidth || current.Length == 0)
            {
                current.Clear().Append(candidate);
            }
            else
            {
                result.Add(current.ToString());
                current.Clear().Append(word);
            }
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result.Count == 0 ? new List<string> { text } : result;
    }
}

/// <summary>A row of pill-shaped metadata chips.</summary>
internal sealed class ChipRow : IStoryLine
{
    /// <summary>Prefix marking a chip that should be drawn with a star.</summary>
    public const char StarMarker = '★';

    private const float ChipHeight = 62f;
    private const float ChipGap = 16f;
    private const float ChipPadding = 26f;
    private const float StarRadius = 15f;
    private const float StarGap = 11f;

    private static readonly SKColor StarColor = new(0xFF, 0xC8, 0x3D);

    private readonly IReadOnlyList<string> _labels;
    private readonly SKFont _font;
    private readonly SKPaint _paint;
    private readonly SKColor _accent;
    private readonly SKColor _fill;

    public ChipRow(
        IReadOnlyList<string> labels,
        SKTypeface typeface,
        SKColor accent,
        SKColor fill,
        SKColor text)
    {
        _labels = labels;
        _accent = accent;
        _fill = fill;
        _font = new SKFont(typeface, 30f) { Edging = SKFontEdging.SubpixelAntialias };
        _paint = new SKPaint { IsAntialias = true, Color = text };
    }

    public float Height => ChipHeight;

    public float SpacingAfter => 30f;

    public void Draw(SKCanvas canvas, float top)
    {
        var chips = _labels.Select(label =>
        {
            var star = label.Length > 0 && label[0] == StarMarker;
            var text = star ? label[1..].TrimStart() : label;
            var extra = star ? (StarRadius * 2) + StarGap : 0f;
            return (Text: text, Star: star, Width: _font.MeasureText(text) + (ChipPadding * 2) + extra);
        }).ToList();

        var totalWidth = chips.Sum(c => c.Width) + (ChipGap * (chips.Count - 1));
        var x = Card.CenterX - (totalWidth / 2f);

        using var fill = new SKPaint { Color = _fill, IsAntialias = true };
        using var stroke = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2f,
            IsAntialias = true,
            Color = _accent.WithAlpha(150)
        };

        var metrics = _font.Metrics;
        var baselineOffset = (ChipHeight - (metrics.Descent + metrics.Ascent)) / 2f;

        foreach (var chip in chips)
        {
            var rect = SKRect.Create(x, top, chip.Width, ChipHeight);
            var rounded = new SKRoundRect(rect, ChipHeight / 2f);
            canvas.DrawRoundRect(rounded, fill);
            canvas.DrawRoundRect(rounded, stroke);

            var textCenter = rect.MidX + (chip.Star ? ((StarRadius * 2) + StarGap) / 2f : 0f);
            canvas.DrawText(chip.Text, textCenter, top + baselineOffset, SKTextAlign.Center, _font, _paint);

            if (chip.Star)
            {
                DrawStar(canvas, rect.Left + ChipPadding + StarRadius, rect.MidY, StarRadius);
            }

            x += chip.Width + ChipGap;
        }
    }

    public void Dispose()
    {
        _font.Dispose();
        _paint.Dispose();
    }

    private static void DrawStar(SKCanvas canvas, float cx, float cy, float outerRadius)
    {
        using var path = new SKPath();
        var innerRadius = outerRadius * 0.44f;

        for (var i = 0; i < 10; i++)
        {
            var radius = i % 2 == 0 ? outerRadius : innerRadius;
            var angle = (-MathF.PI / 2f) + (i * MathF.PI / 5f);
            var point = new SKPoint(cx + (radius * MathF.Cos(angle)), cy + (radius * MathF.Sin(angle)));

            if (i == 0)
            {
                path.MoveTo(point);
            }
            else
            {
                path.LineTo(point);
            }
        }

        path.Close();

        using var paint = new SKPaint { Color = StarColor, IsAntialias = true };
        canvas.DrawPath(path, paint);
    }
}

