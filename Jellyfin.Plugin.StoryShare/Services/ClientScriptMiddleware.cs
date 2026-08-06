using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.StoryShare.Services;

/// <summary>
/// The script tag that loads the web-client half of the plugin, plus the one
/// rule for putting it into a page.
/// </summary>
public static partial class ClientScriptTag
{
    /// <summary>
    /// Relative to /web/index.html, "../StoryShare/ClientScript" resolves to
    /// /StoryShare/ClientScript — which keeps working under a base URL.
    /// </summary>
    public const string Markup =
        "<script plugin=\"StoryShare\" src=\"../StoryShare/ClientScript\" defer></script>";

    [GeneratedRegex(@"\s*<script\s+plugin=""StoryShare""[^>]*>\s*</script>", RegexOptions.IgnoreCase)]
    public static partial Regex ExistingTagRegex();

    /// <summary>
    /// Returns <paramref name="html"/> with exactly one tag before &lt;/body&gt; when
    /// <paramref name="wanted"/>, and none otherwise. Always strips first, so a tag
    /// left behind on disk by an older version can never be doubled up.
    /// </summary>
    public static string Apply(string html, bool wanted)
    {
        var stripped = ExistingTagRegex().Replace(html, string.Empty);
        if (!wanted)
        {
            return stripped;
        }

        var closingBody = stripped.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return closingBody < 0 ? stripped : stripped.Insert(closingBody, Markup);
    }
}

/// <summary>
/// Jellyfin exposes no client-side plugin API, so the Story button has to reach the
/// web client through index.html. Earlier versions wrote the tag into the file on
/// disk, which fails outright on a distro package install — /usr/share/jellyfin/web
/// belongs to root and the server runs as the jellyfin user. This instead injects the
/// tag into the response on its way out, so the plugin never needs write access to
/// anything, and a server update that replaces index.html cannot undo it.
/// </summary>
public sealed class ClientScriptMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ClientScriptMiddleware> _logger;

    public ClientScriptMiddleware(RequestDelegate next, ILogger<ClientScriptMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsIndexRequest(context))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Let a 304 through and there is no body to inject into. Dropping the
        // conditional headers forces the static-file middleware to hand us the
        // whole document every time; it is a few kilobytes.
        context.Request.Headers.Remove(HeaderNames.IfNoneMatch);
        context.Request.Headers.Remove(HeaderNames.IfModifiedSince);

        // Response compression runs downstream of a startup filter, so without this
        // the bytes we capture would be gzip and the injection would corrupt them.
        context.Request.Headers.Remove(HeaderNames.AcceptEncoding);

        // Swapping Response.Body swaps IHttpResponseBodyFeature with it, so the
        // static-file middleware's SendFileAsync lands in the buffer as well.
        var responseBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = responseBody;
        }

        buffer.Position = 0;

        if (context.Response.StatusCode != StatusCodes.Status200OK
            || context.Response.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) != true
            || context.Response.Headers.ContentEncoding.Count > 0)
        {
            await buffer.CopyToAsync(responseBody).ConfigureAwait(false);
            return;
        }

        byte[] payload;
        try
        {
            var wanted = Plugin.Instance?.Configuration.InjectClientScript ?? false;
            payload = Encoding.UTF8.GetBytes(
                ClientScriptTag.Apply(Encoding.UTF8.GetString(buffer.ToArray()), wanted));
        }
        catch (Exception ex)
        {
            // Serving the page unchanged is always better than serving nothing.
            _logger.LogError(ex, "StoryShare: could not add the Story button to index.html");
            buffer.Position = 0;
            await buffer.CopyToAsync(responseBody).ConfigureAwait(false);
            return;
        }

        // The validators the static-file middleware produced describe the file on
        // disk, not what we are about to send, and they would survive the setting
        // being turned off.
        context.Response.Headers.Remove(HeaderNames.ETag);
        context.Response.Headers.Remove(HeaderNames.LastModified);
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.ContentLength = payload.Length;

        await responseBody.WriteAsync(payload).ConfigureAwait(false);
    }

    private static bool IsIndexRequest(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            return false;
        }

        // UsePathBase has already stripped any configured base URL by this point.
        // "/web/" is what UseDefaultFiles later rewrites to "/web/index.html".
        var path = context.Request.Path;
        return path.Equals("/web/", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/web/index.html", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Plugins get a hook into the service collection but not into the request
/// pipeline. An IStartupFilter resolved from that collection is the way across:
/// every filter wraps the server's own Configure, so middleware added before the
/// inner call runs ahead of the static-file middleware that serves index.html.
/// </summary>
public sealed class ClientScriptStartupFilter : IStartupFilter
{
    private readonly ILogger<ClientScriptStartupFilter> _logger;

    public ClientScriptStartupFilter(ILogger<ClientScriptStartupFilter> logger)
    {
        _logger = logger;
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseMiddleware<ClientScriptMiddleware>();
            _logger.LogInformation("StoryShare: web client hook installed");
            next(app);
        };
    }
}
