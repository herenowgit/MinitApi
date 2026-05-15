using System.Text.Json.Serialization;

namespace Workspace.Dtos.Calls;

/// <summary>
/// One entry in the WebRTC ICE-server list. STUN-only entries omit username/credential.
/// </summary>
public sealed class IceServerDto
{
    [JsonPropertyName("urls")]
    public List<string> Urls { get; init; } = new();

    [JsonPropertyName("username")]
    public string? Username { get; init; }

    [JsonPropertyName("credential")]
    public string? Credential { get; init; }
}

/// <summary>
/// Response shape returned to clients. Matches the WebRTC iceServers shape directly
/// so the Android client can pass it through to PeerConnection.RTCConfiguration.
/// </summary>
public sealed class TurnCredentialsResponse
{
    [JsonPropertyName("iceServers")]
    public List<IceServerDto> IceServers { get; init; } = new();

    /// <summary>Seconds until these credentials expire.</summary>
    [JsonPropertyName("ttl")]
    public int Ttl { get; init; }
}
