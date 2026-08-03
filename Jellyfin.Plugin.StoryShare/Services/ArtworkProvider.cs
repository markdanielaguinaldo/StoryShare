using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Jellyfin.Plugin.StoryShare.Services;

/// <summary>
/// Pulls artwork bitmaps off an item, walking up to its parents when the item
/// itself has none (a track usually inherits its cover from the album).
/// </summary>
public class ArtworkProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ArtworkProvider> _logger;

    public ArtworkProvider(IHttpClientFactory httpClientFactory, ILogger<ArtworkProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>Best available square-ish/portrait art: Primary, then Thumb, then Backdrop.</summary>
    public Task<SKBitmap?> GetPrimaryAsync(BaseItem item, CancellationToken cancellationToken) =>
        GetFirstAsync(item, cancellationToken, ImageType.Primary, ImageType.Thumb, ImageType.Backdrop);

    /// <summary>Best available landscape art for the background: Backdrop, then Thumb, then Primary.</summary>
    public Task<SKBitmap?> GetBackdropAsync(BaseItem item, CancellationToken cancellationToken) =>
        GetFirstAsync(item, cancellationToken, ImageType.Backdrop, ImageType.Thumb, ImageType.Primary);

    private async Task<SKBitmap?> GetFirstAsync(
        BaseItem item,
        CancellationToken cancellationToken,
        params ImageType[] types)
    {
        foreach (var candidate in Lineage(item))
        {
            foreach (var type in types)
            {
                var info = candidate.GetImageInfo(type, 0);
                if (info is null)
                {
                    continue;
                }

                var bitmap = await LoadAsync(info, cancellationToken).ConfigureAwait(false);
                if (bitmap is not null)
                {
                    return bitmap;
                }
            }
        }

        return null;
    }

    private static IEnumerable<BaseItem> Lineage(BaseItem item)
    {
        yield return item;

        var current = item;
        // Two hops is enough: track -> album -> artist, episode -> season -> series.
        for (var i = 0; i < 2; i++)
        {
            BaseItem? parent;
            try
            {
                parent = current.GetParent();
            }
            catch (Exception)
            {
                yield break;
            }

            if (parent is null || parent.Id == current.Id)
            {
                yield break;
            }

            yield return parent;
            current = parent;
        }
    }

    private async Task<SKBitmap?> LoadAsync(ItemImageInfo info, CancellationToken cancellationToken)
    {
        try
        {
            if (info.IsLocalFile)
            {
                if (!File.Exists(info.Path))
                {
                    return null;
                }

                await using var stream = File.OpenRead(info.Path);
                return SKBitmap.Decode(stream);
            }

            var client = _httpClientFactory.CreateClient(NamedClient.Default);
            await using var remote = await client.GetStreamAsync(info.Path, cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            await remote.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            buffer.Position = 0;
            return SKBitmap.Decode(buffer);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StoryShare: could not load artwork from {Path}", info.Path);
            return null;
        }
    }
}
