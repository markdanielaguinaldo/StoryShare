using Jellyfin.Plugin.StoryShare.Configuration;
using Jellyfin.Plugin.StoryShare.Models;
using Jellyfin.Plugin.StoryShare.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

var outDir = Path.Combine(AppContext.BaseDirectory, "out");
Directory.CreateDirectory(outDir);

// Synthetic poster (2:3) and album cover (1:1) so layout can be checked without a library.
var posterPath = Path.Combine(outDir, "poster.jpg");
var coverPath = Path.Combine(outDir, "cover.jpg");
MakeArt(posterPath, 800, 1200, new SKColor(0x2B, 0x1B, 0x5E), new SKColor(0xE0, 0x4C, 0x2B));
MakeArt(coverPath, 1000, 1000, new SKColor(0x07, 0x3B, 0x3A), new SKColor(0xF2, 0xC4, 0x3D));

var artwork = new ArtworkProvider(new StubFactory(), NullLogger<ArtworkProvider>.Instance);
var renderer = new StoryCardRenderer(artwork, NullLogger<StoryCardRenderer>.Instance);

var movie = new Movie
{
    Name = "Everything Everywhere All at Once",
    ProductionYear = 2022,
    CommunityRating = 7.8f,
    OfficialRating = "R",
    RunTimeTicks = TimeSpan.FromMinutes(139).Ticks,
    Genres = new[] { "Action", "Adventure", "Comedy" },
    ImageInfos = new[] { new ItemImageInfo { Path = posterPath, Type = ImageType.Primary } }
};

var track = new Audio
{
    Name = "Weird Fishes / Arpeggi",
    Album = "In Rainbows",
    Artists = new[] { "Radiohead" },
    RunTimeTicks = TimeSpan.FromSeconds(318).Ticks,
    Genres = new[] { "Alternative" },
    ImageInfos = new[] { new ItemImageInfo { Path = coverPath, Type = ImageType.Primary } }
};

var longTitle = new Movie
{
    Name = "The Assassination of Jesse James by the Coward Robert Ford",
    ProductionYear = 2007,
    CommunityRating = 7.5f,
    RunTimeTicks = TimeSpan.FromMinutes(160).Ticks,
    Genres = new[] { "Drama", "Western" },
    ImageInfos = new[] { new ItemImageInfo { Path = posterPath, Type = ImageType.Primary } }
};

foreach (var theme in Enum.GetValues<CardTheme>())
{
    await Save($"movie-{theme}", movie, new StoryCardOptions { Theme = theme });
}

await Save("music-poster", track, new StoryCardOptions { Theme = CardTheme.Poster, Comment = "on repeat all week" });
await Save("music-vinyl", track, new StoryCardOptions { Theme = CardTheme.Vinyl, Comment = "on repeat all week" });
await Save("music-polaroid", track, new StoryCardOptions { Theme = CardTheme.Polaroid });
await Save("longtitle-poster", longTitle, new StoryCardOptions { Theme = CardTheme.Poster });
await Save("longtitle-stack", longTitle, new StoryCardOptions { Theme = CardTheme.Stack });
await Save("longtitle-polaroid", longTitle, new StoryCardOptions { Theme = CardTheme.Polaroid, Comment = "a very long comment that has to wrap onto more than one line to be worth testing at all" });

// Every theme has to survive a missing cover — the layouts that build a frame
// around one (Polaroid, Vinyl) are the easy ones to break here.
foreach (var theme in Enum.GetValues<CardTheme>())
{
    var bare = new Movie { Name = "Missing Artwork", ProductionYear = 1999, ImageInfos = Array.Empty<ItemImageInfo>() };
    await Save($"no-artwork-{theme}", bare, new StoryCardOptions { Theme = theme });
}

// Backgrounds: a dark preset, a warm one, a pale one (which must flip the type to
// dark), a raw hex, and a preset over a photographic theme.
await Save("bg-midnight-minimal", movie, new StoryCardOptions { Theme = CardTheme.Minimal, Background = "midnight" });
await Save("bg-ember-vinyl", track, new StoryCardOptions { Theme = CardTheme.Vinyl, Background = "ember" });
await Save("bg-paper-stack", movie, new StoryCardOptions { Theme = CardTheme.Stack, Background = "paper" });
await Save("bg-paper-minimal", movie, new StoryCardOptions { Theme = CardTheme.Minimal, Background = "paper" });
await Save("bg-bone-polaroid", movie, new StoryCardOptions { Theme = CardTheme.Polaroid, Background = "bone" });
await Save("bg-hex-polaroid", movie, new StoryCardOptions { Theme = CardTheme.Polaroid, Background = "#2E1A47" });
await Save("bg-crimson-polaroid", movie, new StoryCardOptions { Theme = CardTheme.Polaroid, Background = "crimson" });
await Save("bg-paper-polaroid", movie, new StoryCardOptions { Theme = CardTheme.Polaroid, Background = "paper" });
await Save("bg-ocean-poster", movie, new StoryCardOptions { Theme = CardTheme.Poster, Background = "ocean" });
await Save("bg-crimson-fullbleed", movie, new StoryCardOptions { Theme = CardTheme.FullBleed, Background = "crimson" });
await Save("bg-nonsense", movie, new StoryCardOptions { Theme = CardTheme.Minimal, Background = "not-a-preset" });

// Animation: raw RGBA frames. Compared pixel-exactly, mid-loop must differ from
// the start (there is motion) while the final frame must be nearly back at the
// start (the loop closes without a visible jump).
var spec = Jellyfin.Plugin.StoryShare.Models.AnimationSpec.Video;
var raw = new MemoryStream();
var animSw = System.Diagnostics.Stopwatch.StartNew();
await renderer.RenderFramesAsync(movie, new StoryCardOptions(), spec, raw, CancellationToken.None);
animSw.Stop();

var frameSize = spec.Width * spec.Height * 4;
var buffer = raw.ToArray();
Console.WriteLine($"frames: {buffer.Length / frameSize} at {spec.Width}x{spec.Height} in {animSw.ElapsedMilliseconds} ms");
Console.WriteLine($"byte count exact                   : {buffer.Length == frameSize * spec.FrameCount}");

double MeanDiff(int a, int b)
{
    long total = 0;
    for (var i = 0; i < frameSize; i++)
    {
        total += Math.Abs(buffer[a * frameSize + i] - buffer[b * frameSize + i]);
    }

    return total / (double)frameSize;
}

var motion = MeanDiff(0, spec.FrameCount / 2);
var seam = MeanDiff(0, spec.FrameCount - 1);
Console.WriteLine($"mid-loop motion (want > 1)         : {motion:F2}");
Console.WriteLine($"loop seam (want much less than mid) : {seam:F2}");
Console.WriteLine($"loop closes cleanly                : {seam < motion / 3}");

// Vinyl spins a full turn instead of pushing in, so it needs a different test: the
// last frame is a whole step short of the first by design, and what has to hold is
// that the seam step is no larger than any other step in the loop. It also runs on
// its own longer spec — that is what makes the record turn slowly.
var spinSpec = Jellyfin.Plugin.StoryShare.Models.AnimationSpec.For(CardTheme.Vinyl);
var spinRaw = new MemoryStream();
var spinSw = System.Diagnostics.Stopwatch.StartNew();
await renderer.RenderFramesAsync(track, new StoryCardOptions { Theme = CardTheme.Vinyl }, spinSpec, spinRaw, CancellationToken.None);
spinSw.Stop();
buffer = spinRaw.ToArray();

var rpm = 60d / (spinSpec.FrameCount / spinSpec.Fps);
Console.WriteLine($"vinyl: {spinSpec.FrameCount} frames @ {spinSpec.Fps} fps = {spinSpec.FrameCount / spinSpec.Fps}s loop, "
    + $"{spinSpec.Duration}s video, {rpm:F1} rpm, drawn in {spinSw.ElapsedMilliseconds} ms");
var spinStep = MeanDiff(0, 1);
var spinSeam = MeanDiff(spinSpec.FrameCount - 1, 0);
var spinHalf = MeanDiff(0, spinSpec.FrameCount / 2);
Console.WriteLine($"vinyl half-turn diff (want > 1)    : {spinHalf:F2}");
Console.WriteLine($"vinyl per-frame step               : {spinStep:F2}");
Console.WriteLine($"vinyl seam step (want <= a step)   : {spinSeam:F2}");
Console.WriteLine($"vinyl loop closes cleanly          : {spinHalf > 1 && spinSeam < spinStep * 1.5}");
Console.WriteLine($"vinyl spin slower than before      : {rpm < 30d}");

Console.WriteLine("Output: " + outDir);
Console.WriteLine();
Console.WriteLine("Share token tests:");
var failures = RenderTest.TokenTests.Run();
Console.WriteLine(failures == 0 ? "All token tests passed." : $"{failures} token test(s) FAILED.");
if (failures > 0)
{
    Environment.ExitCode = 1;
}

async Task Save(string name, MediaBrowser.Controller.Entities.BaseItem item, StoryCardOptions options)
{
    var bytes = await renderer.RenderAsync(item, options, SKEncodedImageFormat.Png, CancellationToken.None);
    var path = Path.Combine(outDir, name + ".png");
    await File.WriteAllBytesAsync(path, bytes);
    Console.WriteLine($"{name,-24} {bytes.Length,8} bytes");
}

static void MakeArt(string path, int w, int h, SKColor a, SKColor b)
{
    using var surface = SKSurface.Create(new SKImageInfo(w, h));
    var canvas = surface.Canvas;
    using (var paint = new SKPaint
    {
        Shader = SKShader.CreateLinearGradient(
            new SKPoint(0, 0), new SKPoint(w, h),
            new[] { a, b }, null, SKShaderTileMode.Clamp)
    })
    {
        canvas.DrawRect(new SKRect(0, 0, w, h), paint);
    }

    using (var circle = new SKPaint { Color = SKColors.White.WithAlpha(60), IsAntialias = true })
    {
        canvas.DrawCircle(w * 0.7f, h * 0.3f, w * 0.25f, circle);
    }

    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
    using var stream = File.OpenWrite(path);
    data.SaveTo(stream);
}

internal sealed class StubFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}
