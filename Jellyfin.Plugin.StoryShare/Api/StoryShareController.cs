using System.Net.Mime;
using Jellyfin.Plugin.StoryShare.Configuration;
using Jellyfin.Plugin.StoryShare.Models;
using Jellyfin.Plugin.StoryShare.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using QRCoder;
using SkiaSharp;

namespace Jellyfin.Plugin.StoryShare.Api;

[ApiController]
[Route("StoryShare")]
// Jellyfin 10.11 dropped the named "DefaultAuthorization" policy and registers its
// default authorization as the fallback policy instead, so a bare [Authorize] is
// what plugin endpoints want. Naming the old policy throws at request time.
[Authorize]
public class StoryShareController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly StoryCardRenderer _renderer;
    private readonly VideoAnimationEncoder _videoEncoder;
    private readonly ShareTokenService _tokens;
    private readonly InstagramStoryPublisher _publisher;
    private readonly ILogger<StoryShareController> _logger;

    public StoryShareController(
        ILibraryManager libraryManager,
        StoryCardRenderer renderer,
        VideoAnimationEncoder videoEncoder,
        ShareTokenService tokens,
        InstagramStoryPublisher publisher,
        ILogger<StoryShareController> logger)
    {
        _libraryManager = libraryManager;
        _renderer = renderer;
        _videoEncoder = videoEncoder;
        _tokens = tokens;
        _publisher = publisher;
        _logger = logger;
    }

    private static PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>Renders the story card for an item. Used by the preview UI.</summary>
    [HttpGet("Items/{itemId}/Card")]
    [Produces(MediaTypeNames.Image.Jpeg, "image/png")]
    public async Task<ActionResult> GetCard(
        [FromRoute] Guid itemId,
        [FromQuery] CardTheme? theme,
        [FromQuery] string? comment,
        [FromQuery] string? background,
        [FromQuery] string? format,
        [FromQuery] bool download,
        CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound();
        }

        return await RenderResult(item, theme, comment, background, format, download, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The card styles and background palettes this server offers. Served so the
    /// settings page and the share dialog do not have to keep their own copies.
    /// </summary>
    [HttpGet("Styles")]
    [Produces(MediaTypeNames.Application.Json)]
    public ActionResult<StyleOptionsResponse> GetStyles()
    {
        var config = Config;

        return new StyleOptionsResponse
        {
            Themes = CardThemes.All
                .Select(t => new ThemeOption
                {
                    Value = (int)t.Theme,
                    Label = t.Label,
                    Description = t.Description
                })
                .ToList(),
            Backgrounds = BackgroundPresets.All
                .Select(b => new BackgroundOption
                {
                    Id = b.Id,
                    Label = b.Label,
                    Top = b.Top,
                    Bottom = b.Bottom,
                    IsLight = b.IsLight
                })
                .ToList(),
            DefaultTheme = (int)config.Theme,
            DefaultBackground = string.IsNullOrWhiteSpace(config.Background)
                ? BackgroundPresets.Auto
                : config.Background
        };
    }

    /// <summary>
    /// Mints a signed, expiring link (plus a QR code) so the card can be opened
    /// on a phone and saved into the Instagram app.
    /// </summary>
    [HttpPost("Items/{itemId}/ShareLink")]
    [Produces(MediaTypeNames.Application.Json)]
    public ActionResult<ShareLinkResponse> CreateShareLink(
        [FromRoute] Guid itemId,
        [FromQuery] CardTheme? theme,
        [FromQuery] string? comment,
        [FromQuery] string? background,
        [FromQuery] string? format,
        [FromQuery] bool includeQr = false)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound();
        }

        var config = Config;
        var lifetime = TimeSpan.FromMinutes(Math.Clamp(config.ShareLinkLifetimeMinutes, 5, 60 * 24 * 7));
        var token = _tokens.Create(itemId, theme, comment, background, lifetime);

        var baseUrl = ResolveBaseUrl(config);
        // The extension is what the anonymous endpoint reads the format back out of.
        var extension = string.Equals(format, "mp4", StringComparison.OrdinalIgnoreCase) ? "mp4"
            : string.Equals(format, "png", StringComparison.OrdinalIgnoreCase) ? "png"
            : "jpg";
        var url = $"{baseUrl}/StoryShare/Public/{token}.{extension}";

        return new ShareLinkResponse
        {
            Url = url,
            DownloadUrl = url + "?download=true",
            ExpiresAt = DateTime.UtcNow.Add(lifetime),
            // Opt-in: this endpoint is hit on every preview load, and rendering a QR
            // PNG for a caller that does not want one is pure waste.
            QrCode = includeQr ? BuildQrDataUri(url) : string.Empty,
            IsPubliclyReachable = !string.IsNullOrWhiteSpace(config.PublicBaseUrl)
        };
    }

    /// <summary>Posts the card straight to the configured Instagram account's story.</summary>
    [HttpPost("Items/{itemId}/Publish")]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<ActionResult<PublishResponse>> Publish(
        [FromRoute] Guid itemId,
        [FromQuery] CardTheme? theme,
        [FromQuery] string? comment,
        [FromQuery] string? background,
        CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound();
        }

        var config = Config;
        if (string.IsNullOrWhiteSpace(config.PublicBaseUrl))
        {
            return new PublishResponse
            {
                Success = false,
                Error = "Set a public base URL in the plugin settings — Instagram's servers download the image themselves, "
                        + "so a LAN address will not work."
            };
        }

        // Meta fetches the image out-of-band, so the token has to outlive the request.
        var token = _tokens.Create(itemId, theme, comment, background, TimeSpan.FromMinutes(20));
        var imageUrl = $"{config.PublicBaseUrl.TrimEnd('/')}/StoryShare/Public/{token}.jpg";

        var result = await _publisher.PublishAsync(imageUrl, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>Reports whether direct publishing is usable, for the settings page.</summary>
    [HttpGet("Status")]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<ActionResult<ConnectionStatusResponse>> GetStatus(CancellationToken cancellationToken) =>
        await _publisher.GetStatusAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Anonymous, signature-gated card delivery. Needed by both the phone handoff
    /// and Instagram's own fetcher, neither of which carries a Jellyfin token.
    /// </summary>
    [HttpGet("Public/{token}")]
    [AllowAnonymous]
    [Produces(MediaTypeNames.Image.Jpeg, "image/png")]
    public async Task<ActionResult> GetPublicCard(
        [FromRoute] string token,
        [FromQuery] bool download,
        CancellationToken cancellationToken)
    {
        // Strip the cosmetic extension that makes the URL look like an image.
        var dot = token.LastIndexOf('.');
        var format = "jpg";
        if (dot > 0)
        {
            format = token[(dot + 1)..];
            token = token[..dot];
        }

        if (!_tokens.TryValidate(token, out var share) || share is null)
        {
            return NotFound();
        }

        var item = _libraryManager.GetItemById(share.ItemId);
        if (item is null)
        {
            return NotFound();
        }

        return await RenderResult(item, share.Theme, share.Comment, share.Background, format, download, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Serves the web UI integration script referenced from index.html.</summary>
    [HttpGet("ClientScript")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public ActionResult GetClientScript()
    {
        var stream = GetType().Assembly.GetManifestResourceStream("Jellyfin.Plugin.StoryShare.Web.storyshare.js");
        if (stream is null)
        {
            return NotFound();
        }

        return File(stream, "application/javascript");
    }

    private async Task<ActionResult> RenderResult(
        BaseItem item,
        CardTheme? theme,
        string? comment,
        string? background,
        string? format,
        bool asAttachment,
        CancellationToken cancellationToken)
    {
        var wantsVideo = string.Equals(format, "mp4", StringComparison.OrdinalIgnoreCase);
        var wantsPng = string.Equals(format, "png", StringComparison.OrdinalIgnoreCase);

        var extension = wantsVideo ? "mp4" : wantsPng ? "png" : "jpg";
        var contentType = wantsVideo ? "video/mp4" : wantsPng ? "image/png" : MediaTypeNames.Image.Jpeg;

        try
        {
            var options = new StoryCardOptions { Theme = theme, Comment = comment, Background = background };

            var bytes = wantsVideo
                ? await _videoEncoder.RenderAsync(item, options, cancellationToken).ConfigureAwait(false)
                : await _renderer.RenderAsync(
                    item,
                    options,
                    wantsPng ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg,
                    cancellationToken).ConfigureAwait(false);

            var fileName = BuildFileName(item.Name, extension);
            Response.Headers.ContentDisposition = BuildContentDisposition(fileName, asAttachment);
            Response.Headers.CacheControl = "private, max-age=60";

            return File(bytes, contentType);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StoryShare: rendering the card for {Item} failed", item.Name);
            return StatusCode(StatusCodes.Status500InternalServerError, "Story card rendering failed. Check the server log.");
        }
    }

    private static string BuildFileName(string? itemName, string extension)
    {
        var name = string.Join('_', (itemName ?? "story").Split(Path.GetInvalidFileNameChars()));
        return $"{name}-story.{extension}";
    }

    /// <summary>
    /// Non-ASCII titles are common in this library (anime, K-pop), and a raw UTF-8
    /// filename in a Content-Disposition header is not legal. Send an ASCII-folded
    /// name plus the RFC 5987 form that modern browsers prefer.
    /// </summary>
    private static string BuildContentDisposition(string fileName, bool asAttachment)
    {
        var disposition = asAttachment ? "attachment" : "inline";

        var ascii = new string(fileName
            .Select(c => c is >= ' ' and <= '~' && c != '"' && c != '\\' ? c : '_')
            .ToArray());

        return $"{disposition}; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(fileName)}";
    }

    private string ResolveBaseUrl(PluginConfiguration config)
    {
        if (!string.IsNullOrWhiteSpace(config.PublicBaseUrl))
        {
            return config.PublicBaseUrl.TrimEnd('/');
        }

        return $"{Request.Scheme}://{Request.Host}{Request.PathBase}".TrimEnd('/');
    }

    private static string BuildQrDataUri(string url)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8);
        return "data:image/png;base64," + Convert.ToBase64String(png);
    }
}
