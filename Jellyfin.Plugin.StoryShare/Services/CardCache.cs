using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.StoryShare.Configuration;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StoryShare.Services;

/// <summary>
/// Keeps finished cards in memory, keyed on everything that can change what one
/// looks like.
///
/// Worth having because the same card is asked for more than once as a matter of
/// course: the share dialog renders a preview, then the link the user opens asks
/// for the identical card again, and a Vinyl video costs ~17s of drawing plus two
/// ffmpeg passes each time. Flipping between styles and back pays the same bill.
///
/// Deliberately stores only *finished* renders — two identical requests arriving at
/// once will both draw, and the second one wins the slot. Sharing an in-flight task
/// between callers would mean one caller's cancellation killing another's render,
/// and the flows that repeat a card are sequential, not concurrent, so there is
/// nothing to gain for the risk.
/// </summary>
public sealed class CardCache
{
    /// <summary>Backstop against a card outliving something the key does not cover.</summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(2);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ServerInfo _server;
    private readonly ILogger<CardCache> _logger;

    private long _bytes;
    private long _clock;

    public CardCache(ServerInfo server, ILogger<CardCache> logger)
    {
        _server = server;
        _logger = logger;
    }

    private sealed record Entry(byte[] Payload, DateTime Stored)
    {
        public long LastUsed { get; set; }
    }

    private static PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    private static long Budget =>
        Math.Clamp(Config.CacheSizeMb, 8, 512) * 1024L * 1024L;

    public bool TryGet(string key, out byte[] payload)
    {
        payload = Array.Empty<byte>();

        if (!Config.CacheEnabled)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return false;
            }

            if (DateTime.UtcNow - entry.Stored > Lifetime)
            {
                Remove(key, entry);
                return false;
            }

            entry.LastUsed = ++_clock;
            payload = entry.Payload;
            return true;
        }
    }

    public void Set(string key, byte[] payload)
    {
        if (!Config.CacheEnabled || payload.Length == 0)
        {
            return;
        }

        var budget = Budget;

        // A single card large enough to sweep the cache clean is not worth keeping;
        // it would evict everything else to cache one thing.
        if (payload.Length > budget / 4)
        {
            return;
        }

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                Remove(key, existing);
            }

            _entries[key] = new Entry(payload, DateTime.UtcNow) { LastUsed = ++_clock };
            _bytes += payload.Length;

            Trim(budget);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _bytes = 0;
        }
    }

    /// <summary>Drops the least recently used cards until the cache is inside budget.</summary>
    private void Trim(long budget)
    {
        if (_bytes <= budget)
        {
            return;
        }

        foreach (var stale in _entries.OrderBy(e => e.Value.LastUsed).ToList())
        {
            Remove(stale.Key, stale.Value);
            _logger.LogDebug(
                "StoryShare: evicted a cached card ({Size} bytes), cache now {Bytes} bytes",
                stale.Value.Payload.Length,
                _bytes);

            if (_bytes <= budget)
            {
                return;
            }
        }
    }

    private void Remove(string key, Entry entry)
    {
        if (_entries.Remove(key))
        {
            _bytes -= entry.Payload.Length;
        }
    }

    /// <summary>
    /// Everything that can change what this card looks like, folded into one string.
    ///
    /// The item's timestamps cover a metadata edit or a new poster, and the config
    /// fingerprint covers a settings change — which is why nothing has to listen for
    /// a configuration-updated event: a card rendered under the old settings simply
    /// stops being reachable.
    ///
    /// The server name is in here on its own account: the footer's <c>{server}</c>
    /// expands to it, so renaming the server changes the card without the configured
    /// footer text changing by a single character.
    /// </summary>
    public string Key(
        BaseItem item,
        CardTheme? theme,
        string? comment,
        string? background,
        CardAnimation? animation,
        string format)
    {
        var config = Config;

        var builder = new StringBuilder()
            .Append(item.Id.ToString("N"))
            .Append('|').Append(item.DateModified.Ticks.ToString(CultureInfo.InvariantCulture))
            .Append('|').Append(item.DateLastSaved.Ticks.ToString(CultureInfo.InvariantCulture))
            .Append('|').Append(theme?.ToString() ?? "default")
            .Append('|').Append(background ?? string.Empty)
            .Append('|').Append(animation?.ToString() ?? "default")
            .Append('|').Append(format)
            .Append('|').Append(comment ?? string.Empty)
            .Append('|').Append(config.Theme)
            .Append('|').Append(config.Animation)
            .Append('|').Append(config.FooterText)
            .Append('|').Append(_server.Name)
            .Append('|').Append(config.AccentColor)
            .Append('|').Append(config.Background)
            .Append('|').Append(config.BackgroundColor)
            .Append('|').Append(config.ShowYear)
            .Append(config.ShowGenres)
            .Append(config.ShowRating)
            .Append(config.ShowRuntime);

        // Hashed so a 180-character caption does not become a 200-byte dictionary key.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash, 0, 16);
    }
}
