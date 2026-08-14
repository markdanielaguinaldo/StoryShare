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
    private readonly CardCache _cache;
    private readonly ILogger<StoryShareController> _logger;

    public StoryShareController(
        ILibraryManager libraryManager,
        StoryCardRenderer renderer,
        VideoAnimationEncoder videoEncoder,
        ShareTokenService tokens,
        CardCache cache,
        ILogger<StoryShareController> logger)
    {
        _libraryManager = libraryManager;
        _renderer = renderer;
        _videoEncoder = videoEncoder;
        _tokens = tokens;
        _cache = cache;
        _logger = logger;
    }

    private static PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Renders a card directly, for an authenticated caller.
    ///
    /// The share dialog does not use this — it mints a signed link and points the
    /// preview at the anonymous endpoint instead, because iOS will only offer
    /// "Save to Photos" on a plain URL. This is the way to render one from a script
    /// without minting a token first.
    /// </summary>
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
    /// Text the dialog can offer to drop into the caption box.
    ///
    /// A suggestion, never an application: the caption stays empty unless the user
    /// asks for this, and once it is in the box it is ordinary editable text. An
    /// item with no tagline returns an empty string and the dialog shows no button.
    /// </summary>
    [HttpGet("Items/{itemId}/Caption")]
    [Produces(MediaTypeNames.Application.Json)]
    public ActionResult<CaptionSuggestionResponse> GetCaptionSuggestion([FromRoute] Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return NotFound();
        }

        var tagline = item.Tagline?.Trim() ?? string.Empty;
        if (tagline.Length > 180)
        {
            tagline = tagline[..180].TrimEnd();
        }

        return new CaptionSuggestionResponse { Tagline = tagline };
    }

    /// <summary>
    /// Mints a signed, expiring link so the card can be opened on a phone and saved
    /// from there.
    /// </summary>
    [HttpPost("Items/{itemId}/ShareLink")]
    [Produces(MediaTypeNames.Application.Json)]
    public ActionResult<ShareLinkResponse> CreateShareLink(
        [FromRoute] Guid itemId,
        [FromQuery] CardTheme? theme,
        [FromQuery] string? comment,
        [FromQuery] string? background,
        [FromQuery] string? format)
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
            IsPubliclyReachable = !string.IsNullOrWhiteSpace(config.PublicBaseUrl)
        };
    }

    /// <summary>
    /// Anonymous, signature-gated card delivery. Needed by the phone handoff, which
    /// carries no Jellyfin token.
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

            // The dialog previews a card and then the opened link asks for exactly
            // the same one, so this is a hit on the request that matters most.
            var key = _cache.Key(item, theme, comment, background, extension);
            if (!_cache.TryGet(key, out var bytes))
            {
                bytes = wantsVideo
                    ? await _videoEncoder.RenderAsync(item, options, cancellationToken).ConfigureAwait(false)
                    : await _renderer.RenderAsync(
                        item,
                        options,
                        wantsPng ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg,
                        cancellationToken).ConfigureAwait(false);

                _cache.Set(key, bytes);
            }

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
}
