using System.Globalization;
using System.Text.Json;
using Jellyfin.Plugin.StoryShare.Configuration;
using Jellyfin.Plugin.StoryShare.Models;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.StoryShare.Services;

/// <summary>
/// Publishes a story through the Instagram Graph API.
///
/// This only works for Instagram Business/Creator accounts — Meta provides no
/// API for posting to a personal account's story, which is why the QR handoff
/// exists alongside it. Meta fetches the image itself, so the URL handed to it
/// must be reachable from the public internet.
/// </summary>
public class InstagramStoryPublisher
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<InstagramStoryPublisher> _logger;

    public InstagramStoryPublisher(IHttpClientFactory httpClientFactory, ILogger<InstagramStoryPublisher> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private static PluginConfiguration Config =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    public async Task<PublishResponse> PublishAsync(string imageUrl, CancellationToken cancellationToken)
    {
        var config = Config;

        if (!config.EnableDirectPublish)
        {
            return Fail("Direct publishing is turned off in the plugin settings.");
        }

        if (string.IsNullOrWhiteSpace(config.InstagramUserId) || string.IsNullOrWhiteSpace(config.InstagramAccessToken))
        {
            return Fail("Instagram account id or access token is not configured.");
        }

        try
        {
            var client = _httpClientFactory.CreateClient(NamedClient.Default);

            // Step 1: create a media container Meta will fill by fetching imageUrl.
            var createUrl = Endpoint(config, $"{config.InstagramUserId}/media");
            using var createBody = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["image_url"] = imageUrl,
                ["media_type"] = "STORIES",
                ["access_token"] = config.InstagramAccessToken
            });

            using var createResponse = await client.PostAsync(createUrl, createBody, cancellationToken).ConfigureAwait(false);
            var createJson = await createResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!createResponse.IsSuccessStatusCode)
            {
                return Fail(ExtractError(createJson) ?? $"Container creation failed ({(int)createResponse.StatusCode}).");
            }

            var creationId = ReadString(createJson, "id");
            if (string.IsNullOrEmpty(creationId))
            {
                return Fail("Instagram did not return a media container id.");
            }

            // Step 2: publish it.
            var publishUrl = Endpoint(config, $"{config.InstagramUserId}/media_publish");
            using var publishBody = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["creation_id"] = creationId,
                ["access_token"] = config.InstagramAccessToken
            });

            using var publishResponse = await client.PostAsync(publishUrl, publishBody, cancellationToken).ConfigureAwait(false);
            var publishJson = await publishResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!publishResponse.IsSuccessStatusCode)
            {
                return Fail(ExtractError(publishJson) ?? $"Publish failed ({(int)publishResponse.StatusCode}).");
            }

            var mediaId = ReadString(publishJson, "id");
            _logger.LogInformation("StoryShare: published story {MediaId}", mediaId);
            return new PublishResponse { Success = true, MediaId = mediaId };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StoryShare: publishing to Instagram failed");
            return Fail(ex.Message);
        }
    }

    public async Task<ConnectionStatusResponse> GetStatusAsync(CancellationToken cancellationToken)
    {
        var config = Config;
        var status = new ConnectionStatusResponse
        {
            DirectPublishEnabled = config.EnableDirectPublish,
            DirectPublishConfigured = !string.IsNullOrWhiteSpace(config.InstagramUserId)
                                      && !string.IsNullOrWhiteSpace(config.InstagramAccessToken),
            HasPublicBaseUrl = !string.IsNullOrWhiteSpace(config.PublicBaseUrl)
        };

        if (!status.DirectPublishConfigured)
        {
            return status;
        }

        try
        {
            var client = _httpClientFactory.CreateClient(NamedClient.Default);
            var url = Endpoint(config, config.InstagramUserId)
                      + $"?fields=username,account_type&access_token={Uri.EscapeDataString(config.InstagramAccessToken)}";

            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                status.Error = ExtractError(json) ?? $"Instagram returned {(int)response.StatusCode}.";
                return status;
            }

            status.Username = ReadString(json, "username");
            status.AccountType = ReadString(json, "account_type");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            status.Error = ex.Message;
        }

        return status;
    }

    private static string Endpoint(PluginConfiguration config, string path)
    {
        var version = string.IsNullOrWhiteSpace(config.GraphApiVersion) ? "v21.0" : config.GraphApiVersion.Trim();
        return string.Create(CultureInfo.InvariantCulture, $"https://graph.facebook.com/{version}/{path}");
    }

    private static PublishResponse Fail(string error) => new() { Success = false, Error = error };

    private static string? ReadString(string json, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty(property, out var value) ? value.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ExtractError(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                var message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
                var subcode = error.TryGetProperty("error_user_msg", out var u) ? u.GetString() : null;
                return subcode ?? message;
            }
        }
        catch (JsonException)
        {
            // Fall through to null so the caller reports the status code instead.
        }

        return null;
    }
}
