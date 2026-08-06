using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StoryShare.Services;

/// <summary>
/// Versions up to 1.0.0.1 wrote the script tag into jellyfin-web's index.html and
/// left an index.html.storyshare.bak next to it. <see cref="ClientScriptMiddleware"/>
/// injects the tag into the response now, so anything left on disk is at best
/// redundant. Removing it is best effort by design: on the installs that made this
/// change necessary the file is not writable, and the middleware strips a stale tag
/// out of the response anyway.
/// </summary>
public class LegacyTagCleanupService : IHostedService
{
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<LegacyTagCleanupService> _logger;

    public LegacyTagCleanupService(IApplicationPaths appPaths, ILogger<LegacyTagCleanupService> logger)
    {
        _appPaths = appPaths;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Clean();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "StoryShare: could not tidy up the old index.html script tag");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void Clean()
    {
        var indexPath = Path.Combine(_appPaths.WebPath, "index.html");
        if (!File.Exists(indexPath))
        {
            return;
        }

        var original = File.ReadAllText(indexPath);
        var stripped = ClientScriptTag.ExistingTagRegex().Replace(original, string.Empty);
        if (string.Equals(stripped, original, StringComparison.Ordinal))
        {
            return;
        }

        File.WriteAllText(indexPath, stripped);

        var backup = indexPath + ".storyshare.bak";
        if (File.Exists(backup))
        {
            File.Delete(backup);
        }

        _logger.LogInformation(
            "StoryShare: removed the script tag this plugin used to write into index.html. "
            + "The Story button is served from the plugin now and needs no file access.");
    }
}
