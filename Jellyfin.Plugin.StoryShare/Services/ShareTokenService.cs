using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.StoryShare.Configuration;

namespace Jellyfin.Plugin.StoryShare.Services;

/// <summary>
/// Mints and validates short-lived, HMAC-signed tokens so a story card can be
/// fetched without a Jellyfin session — needed both for the phone handoff (QR)
/// and for Meta's servers, which fetch the image anonymously.
/// </summary>
public class ShareTokenService
{
    public record ShareToken(
        Guid ItemId,
        CardTheme? Theme,
        string? Comment,
        string? Background,
        CardAnimation? Animation,
        DateTime ExpiresAt);

    private static PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? throw new InvalidOperationException("Plugin not loaded.");

    public string Create(
        Guid itemId,
        CardTheme? theme,
        string? comment,
        string? background,
        CardAnimation? animation,
        TimeSpan lifetime)
    {
        var expires = DateTimeOffset.UtcNow.Add(lifetime).ToUnixTimeSeconds();

        // Everything new is appended rather than slotted in beside the field it
        // belongs with, so links minted before it existed still parse.
        var payload = string.Join(
            '|',
            itemId.ToString("N"),
            theme.HasValue ? ((int)theme.Value).ToString(CultureInfo.InvariantCulture) : string.Empty,
            Encode(Encoding.UTF8.GetBytes(comment ?? string.Empty)),
            expires.ToString(CultureInfo.InvariantCulture),
            Encode(Encoding.UTF8.GetBytes(background ?? string.Empty)),
            animation.HasValue ? ((int)animation.Value).ToString(CultureInfo.InvariantCulture) : string.Empty);

        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        return Encode(payloadBytes) + "." + Encode(Sign(payloadBytes));
    }

    public bool TryValidate(string token, out ShareToken? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var dot = token.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || dot == token.Length - 1)
        {
            return false;
        }

        byte[] payloadBytes;
        byte[] signature;
        try
        {
            payloadBytes = Decode(token[..dot]);
            signature = Decode(token[(dot + 1)..]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (!CryptographicOperations.FixedTimeEquals(signature, Sign(payloadBytes)))
        {
            return false;
        }

        var parts = Encoding.UTF8.GetString(payloadBytes).Split('|');
        if (parts.Length is not (4 or 5 or 6)
            || !Guid.TryParseExact(parts[0], "N", out var itemId)
            || !long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var expiresUnix))
        {
            return false;
        }

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(expiresUnix);
        if (expiresAt < DateTimeOffset.UtcNow)
        {
            return false;
        }

        CardTheme? theme = null;
        if (!string.IsNullOrEmpty(parts[1])
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var themeValue)
            && Enum.IsDefined(typeof(CardTheme), themeValue))
        {
            theme = (CardTheme)themeValue;
        }

        CardAnimation? animation = null;
        if (parts.Length > 5
            && !string.IsNullOrEmpty(parts[5])
            && int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var animationValue)
            && Enum.IsDefined(typeof(CardAnimation), animationValue))
        {
            animation = (CardAnimation)animationValue;
        }

        string? comment;
        string? background;
        try
        {
            comment = DecodeText(parts[2]);
            background = parts.Length > 4 ? DecodeText(parts[4]) : null;
        }
        catch (FormatException)
        {
            return false;
        }

        result = new ShareToken(itemId, theme, comment, background, animation, expiresAt.UtcDateTime);
        return true;
    }

    private static byte[] Sign(byte[] payload)
    {
        var key = Convert.FromBase64String(Config.SigningKey);
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(payload);
    }

    private static string? DecodeText(string value) =>
        string.IsNullOrEmpty(value) ? null : Encoding.UTF8.GetString(Decode(value));

    // base64url, so tokens survive being pasted into a URL.
    private static string Encode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Decode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight((padded.Length + 3) / 4 * 4, '='));
    }
}
