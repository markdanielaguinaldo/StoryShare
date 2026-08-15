using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.StoryShare;
using Jellyfin.Plugin.StoryShare.Configuration;
using Jellyfin.Plugin.StoryShare.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;

namespace RenderTest;

internal static class TokenTests
{
    public static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "storyshare-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "configurations"));

        // Constructing the plugin gives ShareTokenService a real signing key.
        _ = new Plugin(new StubPaths(root), new StubXmlSerializer());

        var tokens = new ShareTokenService();
        var itemId = Guid.NewGuid();
        var failures = 0;

        var valid = tokens.Create(
            itemId,
            CardTheme.FullBleed,
            "hello world",
            "midnight",
            CardAnimation.Pulse,
            TimeSpan.FromMinutes(30));
        failures += Check(
            "round-trips a valid token",
            tokens.TryValidate(valid, out var parsed)
            && parsed!.ItemId == itemId
            && parsed.Theme == CardTheme.FullBleed
            && parsed.Comment == "hello world"
            && parsed.Background == "midnight"
            && parsed.Animation == CardAnimation.Pulse);

        // A background that is a raw colour has to survive the round trip too.
        var hexBackground = tokens.Create(
            itemId,
            CardTheme.Vinyl,
            null,
            "#2E1A47",
            null,
            TimeSpan.FromMinutes(30));
        failures += Check(
            "round-trips a hex background",
            tokens.TryValidate(hexBackground, out var hexParsed)
            && hexParsed!.Background == "#2E1A47"
            && hexParsed.Theme == CardTheme.Vinyl
            && hexParsed.Comment is null);

        // An unset animation has to come back unset, not as Auto: the render falls
        // back to the configured default, which may well not be Auto.
        failures += Check(
            "an omitted animation stays omitted",
            tokens.TryValidate(hexBackground, out var noAnimation) && noAnimation!.Animation is null);

        // Links minted before the animation field existed carry five fields, and
        // they have to keep working until they expire.
        var legacy = LegacyToken(itemId, CardTheme.Stack, "older link", "ember");
        failures += Check(
            "still reads a link minted before animations",
            tokens.TryValidate(legacy, out var legacyParsed)
            && legacyParsed!.Theme == CardTheme.Stack
            && legacyParsed.Comment == "older link"
            && legacyParsed.Background == "ember"
            && legacyParsed.Animation is null);

        failures += Check("token is URL-safe", !valid.Contains('+') && !valid.Contains('/') && !valid.Contains('='));

        // Flip a character mid-payload; the signature must no longer match.
        // (Not the last character — trailing base64 bits can decode identically.)
        var dot = valid.IndexOf('.');
        failures += Check("rejects a tampered payload", !tokens.TryValidate(Flip(valid, dot / 2), out _));

        // And a flipped signature must be rejected too.
        failures += Check("rejects a tampered signature", !tokens.TryValidate(Flip(valid, dot + 2), out _));

        // Re-sign with a different key: the old token must stop validating.
        var expired = tokens.Create(itemId, null, null, null, null, TimeSpan.FromSeconds(-30));
        failures += Check("rejects an expired token", !tokens.TryValidate(expired, out _));

        Plugin.Instance!.Configuration.SigningKey =
            Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        failures += Check("rotating the key invalidates old links", !tokens.TryValidate(valid, out _));

        failures += Check("rejects junk", !tokens.TryValidate("not-a-token", out _));
        failures += Check("rejects empty", !tokens.TryValidate(string.Empty, out _));

        try
        {
            Directory.Delete(root, true);
        }
        catch (IOException)
        {
            // Leftover temp dir is harmless.
        }

        return failures;
    }

    /// <summary>
    /// A token in the five-field shape this plugin minted before the animation was
    /// part of one, signed with the live key — the only way to prove an outstanding
    /// link still opens after this change.
    /// </summary>
    private static string LegacyToken(Guid itemId, CardTheme theme, string comment, string background)
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds();
        var payload = string.Join(
            '|',
            itemId.ToString("N"),
            ((int)theme).ToString(CultureInfo.InvariantCulture),
            Encode(Encoding.UTF8.GetBytes(comment)),
            expires.ToString(CultureInfo.InvariantCulture),
            Encode(Encoding.UTF8.GetBytes(background)));

        var bytes = Encoding.UTF8.GetBytes(payload);
        using var hmac = new HMACSHA256(Convert.FromBase64String(Plugin.Instance!.Configuration.SigningKey));
        return Encode(bytes) + "." + Encode(hmac.ComputeHash(bytes));
    }

    private static string Encode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Flip(string token, int index)
    {
        var chars = token.ToCharArray();
        chars[index] = chars[index] == 'A' ? 'B' : 'A';
        return new string(chars);
    }

    private static int Check(string name, bool passed)
    {
        Console.WriteLine($"  [{(passed ? "PASS" : "FAIL")}] {name}");
        return passed ? 0 : 1;
    }

    private sealed class StubPaths : IApplicationPaths
    {
        private readonly string _root;

        public StubPaths(string root) => _root = root;

        public string ProgramDataPath => _root;
        public string WebPath => Path.Combine(_root, "web");
        public string ProgramSystemPath => _root;
        public string DataPath => Path.Combine(_root, "data");
        public string ImageCachePath => Path.Combine(_root, "imagecache");
        public string PluginsPath => Path.Combine(_root, "plugins");
        public string PluginConfigurationsPath => Path.Combine(_root, "configurations");
        public string LogDirectoryPath => Path.Combine(_root, "log");
        public string ConfigurationDirectoryPath => Path.Combine(_root, "config");
        public string SystemConfigurationFilePath => Path.Combine(_root, "config", "system.xml");
        public string CachePath { get; set; } = Path.Combine(Path.GetTempPath(), "storyshare-cache");
        public string TempDirectory => Path.Combine(Path.GetTempPath(), "storyshare-temp");
        public string VirtualDataPath => "%AppDataPath%";
        public string TrickplayPath => Path.Combine(_root, "trickplay");
        public string BackupPath => Path.Combine(_root, "backup");

        public void MakeSanityCheckOrThrow()
        {
        }

        public void CreateAndCheckMarker(string path, string markerName, bool recursive = false)
        {
        }
    }

    /// <summary>
    /// No config file exists in the test. Throwing makes BasePlugin fall back to a
    /// freshly constructed PluginConfiguration, which is what a first run does.
    /// </summary>
    private sealed class StubXmlSerializer : IXmlSerializer
    {
        public object DeserializeFromStream(Type type, Stream stream) => throw new FileNotFoundException();

        public object DeserializeFromFile(Type type, string file) => throw new FileNotFoundException(file);

        public object DeserializeFromBytes(Type type, byte[] buffer) => throw new FileNotFoundException();

        public void SerializeToStream(object obj, Stream stream)
        {
        }

        public void SerializeToFile(object obj, string file)
        {
        }
    }
}
