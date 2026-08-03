using System.Diagnostics;
using System.Globalization;
using Jellyfin.Plugin.StoryShare.Configuration;
using Jellyfin.Plugin.StoryShare.Models;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StoryShare.Services;

/// <summary>
/// Encodes the animated card as an MP4 with the ffmpeg Jellyfin already ships.
///
/// Instagram flattens a GIF added to a story into a still image, so video is the
/// only way to get motion into a story. The output is deliberately conservative
/// about what Stories accepts: 1080x1920, H.264 High, yuv420p, and a silent AAC
/// track — Instagram is unreliable with video that carries no audio stream at all.
///
/// Two passes. The first pipes raw RGBA frames straight in and writes one loop;
/// the second repeats that loop and muxes in the silence. Repeating in ffmpeg
/// rather than re-drawing keeps a six second clip at the cost of two seconds of
/// rendering, and -stream_loop cannot read from a pipe, hence the temp file.
/// </summary>
public class VideoAnimationEncoder
{
    private static readonly TimeSpan EncodeTimeout = TimeSpan.FromSeconds(120);

    private readonly StoryCardRenderer _renderer;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<VideoAnimationEncoder> _logger;

    public VideoAnimationEncoder(
        StoryCardRenderer renderer,
        IMediaEncoder mediaEncoder,
        IApplicationPaths appPaths,
        ILogger<VideoAnimationEncoder> logger)
    {
        _renderer = renderer;
        _mediaEncoder = mediaEncoder;
        _appPaths = appPaths;
        _logger = logger;
    }

    public async Task<byte[]> RenderAsync(
        BaseItem item,
        StoryCardOptions options,
        CancellationToken cancellationToken)
    {
        var ffmpeg = _mediaEncoder.EncoderPath;
        if (string.IsNullOrWhiteSpace(ffmpeg))
        {
            throw new InvalidOperationException(
                "No ffmpeg is configured on this server, so animated cards cannot be produced.");
        }

        // Vinyl needs a longer loop to spin slowly and still meet itself at the seam.
        var theme = options.Theme ?? Plugin.Instance?.Configuration.Theme ?? CardTheme.Poster;
        var spec = AnimationSpec.For(theme);
        var started = Stopwatch.StartNew();

        var workDir = Path.Combine(_appPaths.TempDirectory, "storyshare-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);

        var loopPath = Path.Combine(workDir, "loop.mp4");
        var outputPath = Path.Combine(workDir, "story.mp4");

        try
        {
            var renderMs = await EncodeLoopAsync(ffmpeg, spec, item, options, loopPath, cancellationToken)
                .ConfigureAwait(false);
            var encodedMs = started.ElapsedMilliseconds;

            await RepeatAndAddSilenceAsync(ffmpeg, spec, loopPath, outputPath, cancellationToken)
                .ConfigureAwait(false);

            var bytes = await File.ReadAllBytesAsync(outputPath, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "StoryShare: video for {Name} — {Duration}s at {Width}x{Height} in {Elapsed} ms "
                + "({Render} ms drawing {Frames} frames, {Encode} ms encoding, {Mux} ms muxing), {Size} bytes",
                item.Name,
                spec.Duration,
                spec.Width,
                spec.Height,
                started.ElapsedMilliseconds,
                renderMs,
                spec.FrameCount,
                encodedMs - renderMs,
                started.ElapsedMilliseconds - encodedMs,
                bytes.Length);

            return bytes;
        }
        finally
        {
            try
            {
                Directory.Delete(workDir, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "StoryShare: could not clean up {Dir}", workDir);
            }
        }
    }

    /// <summary>Renders one loop straight into ffmpeg. Returns ms spent drawing.</summary>
    private async Task<long> EncodeLoopAsync(
        string ffmpeg,
        AnimationSpec spec,
        BaseItem item,
        StoryCardOptions options,
        string loopPath,
        CancellationToken cancellationToken)
    {
        var startInfo = NewFfmpeg(ffmpeg);
        startInfo.RedirectStandardInput = true;

        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("rawvideo");
        startInfo.ArgumentList.Add("-pixel_format");
        startInfo.ArgumentList.Add("rgba");
        startInfo.ArgumentList.Add("-video_size");
        startInfo.ArgumentList.Add(string.Create(CultureInfo.InvariantCulture, $"{spec.Width}x{spec.Height}"));
        startInfo.ArgumentList.Add("-framerate");
        startInfo.ArgumentList.Add(spec.Fps.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add("pipe:0");

        startInfo.ArgumentList.Add("-c:v");
        startInfo.ArgumentList.Add("libx264");
        startInfo.ArgumentList.Add("-profile:v");
        startInfo.ArgumentList.Add("high");
        // yuv420p is not optional: players and Instagram both reject 4:4:4 H.264.
        startInfo.ArgumentList.Add("-pix_fmt");
        startInfo.ArgumentList.Add("yuv420p");
        startInfo.ArgumentList.Add("-crf");
        startInfo.ArgumentList.Add("18");
        startInfo.ArgumentList.Add("-preset");
        startInfo.ArgumentList.Add("veryfast");
        // Keyframe every second, so the repeated copies splice cleanly.
        startInfo.ArgumentList.Add("-g");
        startInfo.ArgumentList.Add(((int)spec.Fps).ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(loopPath);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start ffmpeg for the animated card.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(EncodeTimeout);

        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);

        var drawTimer = Stopwatch.StartNew();
        try
        {
            await _renderer
                .RenderFramesAsync(item, options, spec, process.StandardInput.BaseStream, timeout.Token)
                .ConfigureAwait(false);
            drawTimer.Stop();

            // Closing stdin is what tells ffmpeg the sequence is complete.
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new InvalidOperationException("ffmpeg timed out while building the animated card.");
        }
        catch (IOException ex)
        {
            TryKill(process);
            var reason = await SafeAsync(stderrTask).ConfigureAwait(false);
            _logger.LogError(ex, "StoryShare: ffmpeg pipe broke. Output:\n{Stderr}", reason);
            throw new InvalidOperationException("ffmpeg closed the connection while building the animated card.");
        }

        await ThrowIfFailedAsync(process, stderrTask, stdoutTask).ConfigureAwait(false);
        return drawTimer.ElapsedMilliseconds;
    }

    /// <summary>Repeats the loop to full length and adds a silent audio track.</summary>
    private async Task RepeatAndAddSilenceAsync(
        string ffmpeg,
        AnimationSpec spec,
        string loopPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var startInfo = NewFfmpeg(ffmpeg);

        startInfo.ArgumentList.Add("-stream_loop");
        startInfo.ArgumentList.Add((spec.LoopCount - 1).ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(loopPath);

        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("lavfi");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add("anullsrc=channel_layout=stereo:sample_rate=44100");

        // The video is already H.264 in the right pixel format, so just remux it.
        startInfo.ArgumentList.Add("-c:v");
        startInfo.ArgumentList.Add("copy");
        startInfo.ArgumentList.Add("-c:a");
        startInfo.ArgumentList.Add("aac");
        startInfo.ArgumentList.Add("-b:a");
        startInfo.ArgumentList.Add("96k");
        // anullsrc is infinite; stop when the looped video ends.
        startInfo.ArgumentList.Add("-shortest");
        startInfo.ArgumentList.Add("-movflags");
        startInfo.ArgumentList.Add("+faststart");
        startInfo.ArgumentList.Add(outputPath);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start ffmpeg to finish the animated card.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(EncodeTimeout);

        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new InvalidOperationException("ffmpeg timed out while finishing the animated card.");
        }

        await ThrowIfFailedAsync(process, stderrTask, stdoutTask).ConfigureAwait(false);

        if (!File.Exists(outputPath))
        {
            throw new InvalidOperationException("ffmpeg produced no output for the animated card.");
        }
    }

    private static ProcessStartInfo NewFfmpeg(string ffmpeg)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-nostdin");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-y");
        return startInfo;
    }

    private async Task ThrowIfFailedAsync(Process process, Task<string> stderrTask, Task<string> stdoutTask)
    {
        var stderr = await SafeAsync(stderrTask).ConfigureAwait(false);
        await SafeAsync(stdoutTask).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            _logger.LogError("StoryShare: ffmpeg exited {Code}. Output:\n{Stderr}", process.ExitCode, stderr);
            throw new InvalidOperationException($"ffmpeg failed with exit code {process.ExitCode}.");
        }
    }

    private static async Task<string> SafeAsync(Task<string> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            return "(stderr unavailable)";
        }
    }

    private void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StoryShare: could not kill the ffmpeg process");
        }
    }
}
