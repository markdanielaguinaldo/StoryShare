using System.Text.RegularExpressions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StoryShare.Services;

/// <summary>
/// Jellyfin has no extension point for adding buttons to item pages, so the
/// supported-by-convention approach is to add one script tag to jellyfin-web's
/// index.html. Opt-in, idempotent, backed up, and removed cleanly when disabled.
/// </summary>
public partial class ScriptInjectionService : IHostedService
{
    private const string ScriptTag = "<script plugin=\"StoryShare\" src=\"../StoryShare/ClientScript\" defer></script>";

    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<ScriptInjectionService> _logger;

    public ScriptInjectionService(IApplicationPaths appPaths, ILogger<ScriptInjectionService> logger)
    {
        _appPaths = appPaths;
        _logger = logger;
    }

    [GeneratedRegex(@"\s*<script\s+plugin=""StoryShare""[^>]*>\s*</script>", RegexOptions.IgnoreCase)]
    private static partial Regex ExistingTagRegex();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Apply();

        if (Plugin.Instance is not null)
        {
            Plugin.Instance.ConfigurationChanged += OnConfigurationChanged;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (Plugin.Instance is not null)
        {
            Plugin.Instance.ConfigurationChanged -= OnConfigurationChanged;
        }

        return Task.CompletedTask;
    }

    private void OnConfigurationChanged(object? sender, BasePluginConfiguration e) => Apply();

    private void Apply()
    {
        var wanted = Plugin.Instance?.Configuration.InjectClientScript ?? false;

        string indexPath;
        try
        {
            indexPath = Path.Combine(_appPaths.WebPath, "index.html");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StoryShare: could not resolve the jellyfin-web path");
            return;
        }

        if (!File.Exists(indexPath))
        {
            _logger.LogWarning(
                "StoryShare: {Path} not found, skipping script injection. The plugin's own page still works.",
                indexPath);
            return;
        }

        try
        {
            var original = File.ReadAllText(indexPath);
            var stripped = ExistingTagRegex().Replace(original, string.Empty);

            string updated;
            if (wanted)
            {
                var closingBody = stripped.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                if (closingBody < 0)
                {
                    _logger.LogWarning("StoryShare: no </body> in index.html, skipping script injection");
                    return;
                }

                updated = stripped.Insert(closingBody, ScriptTag);
            }
            else
            {
                updated = stripped;
            }

            if (string.Equals(updated, original, StringComparison.Ordinal))
            {
                return;
            }

            var backup = indexPath + ".storyshare.bak";
            if (!File.Exists(backup))
            {
                File.Copy(indexPath, backup);
            }

            File.WriteAllText(indexPath, updated);
            _logger.LogInformation(
                "StoryShare: {Action} the web UI script tag. Reload the Jellyfin web client to pick it up.",
                wanted ? "added" : "removed");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(
                ex,
                "StoryShare: no write access to index.html, so the Share button was not injected. "
                + "Use the plugin's own page instead, or make {Path} writable by the Jellyfin user.",
                indexPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StoryShare: failed to update index.html");
        }
    }
}
