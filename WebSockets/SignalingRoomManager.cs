using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace Workspace.WebSockets;

/// <summary>
/// Tracks all active WebSocket peers grouped by call ID.
/// Thread-safe — safe to call from concurrent request handlers.
/// </summary>
public sealed class SignalingRoomManager
{
    // callId → { userId → WebSocket }
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, WebSocket>> _rooms = new();

    /// <summary>Adds a peer to a room. Replaces any stale socket for the same userId.</summary>
    public void Join(string callId, string userId, WebSocket socket)
    {
        var room = _rooms.GetOrAdd(callId, _ => new ConcurrentDictionary<string, WebSocket>());
        room[userId] = socket;
    }

    /// <summary>Removes a peer from a room. Cleans up the room if it becomes empty.</summary>
    public void Leave(string callId, string userId)
    {
        if (!_rooms.TryGetValue(callId, out var room)) return;

        room.TryRemove(userId, out _);

        if (room.IsEmpty)
            _rooms.TryRemove(callId, out _);
    }

    /// <summary>
    /// Sends <paramref name="data"/> to every peer in <paramref name="callId"/>
    /// except the sender identified by <paramref name="senderUserId"/>.
    /// </summary>
    public async Task BroadcastAsync(
        string callId,
        string senderUserId,
        ArraySegment<byte> data,
        WebSocketMessageType messageType,
        CancellationToken ct)
    {
        if (!_rooms.TryGetValue(callId, out var room)) return;

        var sends = room
            .Where(kv => kv.Key != senderUserId && kv.Value.State == WebSocketState.Open)
            .Select(kv => kv.Value.SendAsync(data, messageType, endOfMessage: true, ct));

        await Task.WhenAll(sends);
    }

    /// <summary>Returns how many peers are currently in a room (useful for diagnostics).</summary>
    public int PeerCount(string callId) =>
        _rooms.TryGetValue(callId, out var room) ? room.Count : 0;
}
