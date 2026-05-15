using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Workspace.Dtos.Calls;

namespace Workspace.Services;

public interface ITurnService
{
    /// <summary>
    /// Mints short-lived TURN credentials from Cloudflare Calls.
    /// Returns null if Cloudflare is not configured or the request fails — the
    /// client will fall back to STUN-only ICE, which works in ~70-80% of cases.
    /// </summary>
    Task<TurnCredentialsResponse?> MintCredentialsAsync(int ttlSeconds, CancellationToken ct);
}

public sealed class TurnService(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<TurnService> logger) : ITurnService
{
    public const string HttpClientName = "cloudflare-turn";

    private const string EnvKeyId = "CLOUDFLARE_TURN_KEY_ID";
    private const string EnvApiToken = "CLOUDFLARE_TURN_API_TOKEN";

    public async Task<TurnCredentialsResponse?> MintCredentialsAsync(int ttlSeconds, CancellationToken ct)
    {
        var keyId = Environment.GetEnvironmentVariable(EnvKeyId) ?? configuration[$"Cloudflare:Turn:KeyId"];
        var apiToken = Environment.GetEnvironmentVariable(EnvApiToken) ?? configuration[$"Cloudflare:Turn:ApiToken"];

        if (string.IsNullOrWhiteSpace(keyId) || string.IsNullOrWhiteSpace(apiToken))
        {
            logger.LogWarning("TurnService: Cloudflare TURN not configured — set {EnvKeyId} and {EnvApiToken}",
                EnvKeyId, EnvApiToken);
            return null;
        }

        var http = httpClientFactory.CreateClient(HttpClientName);
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://rtc.live.cloudflare.com/v1/turn/keys/{keyId}/credentials/generate-ice-servers");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
        request.Content = JsonContent.Create(new { ttl = ttlSeconds });

        try
        {
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                logger.LogWarning("TurnService: Cloudflare returned {Status}: {Body}",
                    (int)response.StatusCode, body.Length > 500 ? body[..500] : body);
                return null;
            }

            var payload = await response.Content.ReadFromJsonAsync<CloudflareTurnResponse>(cancellationToken: ct);
            if (payload?.IceServers is null || payload.IceServers.Count == 0)
            {
                logger.LogWarning("TurnService: Cloudflare returned empty iceServers");
                return null;
            }

            return new TurnCredentialsResponse
            {
                IceServers = payload.IceServers,
                Ttl = ttlSeconds
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "TurnService: failed to mint Cloudflare TURN credentials");
            return null;
        }
    }

    private sealed class CloudflareTurnResponse
    {
        [JsonPropertyName("iceServers")]
        public List<IceServerDto>? IceServers { get; init; }
    }
}
