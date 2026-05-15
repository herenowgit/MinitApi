using System.Net.WebSockets;
using System.Text;

namespace Workspace.WebSockets;

public static class SignalingHandler
{
    // Max size of a single WebRTC signaling message.
    // SDP bodies can be ~8 KB; ICE candidates are tiny. 64 KB is generous headroom.
    private const int BufferSize = 64 * 1024;

    /// <summary>
    /// Accepts a WebSocket connection, registers the peer in a signaling room,
    /// and relays every incoming message to all other peers in that room until
    /// the socket closes.
    /// </summary>
    public static async Task HandleAsync(
        HttpContext context,
        SignalingRoomManager roomManager,
        ILogger logger,
        CancellationToken ct)
    {
        // ── Validate query params ─────────────────────────────────────────────
        var callId = context.Request.Query["callId"].ToString();
        var userId = context.Request.Query["userId"].ToString();

        if (string.IsNullOrWhiteSpace(callId) || string.IsNullOrWhiteSpace(userId))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("callId and userId query parameters are required.", ct);
            return;
        }

        // Snapshot existing peers BEFORE we add ourselves so we can tell the
        // new peer about whoever was already in the room.
        var existingPeerIds = roomManager.PeerUserIds(callId);

        // ── Upgrade to WebSocket ──────────────────────────────────────────────
        using var ws = await context.WebSockets.AcceptWebSocketAsync();
        roomManager.Join(callId, userId, ws);
        logger.LogInformation("Signaling: peer {UserId} joined room {CallId} ({PeerCount} peers)",
            userId, callId, roomManager.PeerCount(callId));

        // Notify existing peers (e.g. the caller) that the new peer (callee) joined.
        // The caller waits for this before sending the SDP offer to avoid a race
        // where the offer is broadcast to an empty room.
        var joinedFromNewJson = $"{{\"type\":\"peer_joined\",\"from\":\"{userId}\",\"callId\":\"{callId}\"}}";
        var joinedFromNewBytes = new ArraySegment<byte>(Encoding.UTF8.GetBytes(joinedFromNewJson));
        await roomManager.BroadcastAsync(callId, userId, joinedFromNewBytes, WebSocketMessageType.Text, ct);

        // Also tell the new peer about anyone who was already in the room.
        // Without this the new peer hangs forever if it joined second but is
        // the SDP-offer sender (e.g. caller's FCM was processed after callee's).
        foreach (var existingId in existingPeerIds)
        {
            var joinedFromExistingJson = $"{{\"type\":\"peer_joined\",\"from\":\"{existingId}\",\"callId\":\"{callId}\"}}";
            var joinedFromExistingBytes = new ArraySegment<byte>(Encoding.UTF8.GetBytes(joinedFromExistingJson));
            await roomManager.SendToAsync(callId, userId, joinedFromExistingBytes, WebSocketMessageType.Text, ct);
        }

        var buffer = new byte[BufferSize];

        try
        {
            while (ws.State == WebSocketState.Open)
            {
                // ── Read one complete message ─────────────────────────────────
                var received = await ReceiveFullMessageAsync(ws, buffer, ct);

                if (received.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by peer", ct);
                    break;
                }

                if (received.Count == 0) continue;

                // ── Relay to everyone else in the room ────────────────────────
                var segment = new ArraySegment<byte>(buffer, 0, received.Count);
                await roomManager.BroadcastAsync(callId, userId, segment, received.MessageType, ct);
            }
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            // Client disconnected abruptly — not an error worth logging loudly
            logger.LogDebug("Signaling: peer {UserId} disconnected abruptly from room {CallId}", userId, callId);
        }
        catch (OperationCanceledException)
        {
            // Server shutting down
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Signaling: unexpected error for peer {UserId} in room {CallId}", userId, callId);
        }
        finally
        {
            roomManager.Leave(callId, userId);
            logger.LogInformation("Signaling: peer {UserId} left room {CallId} ({PeerCount} peers remaining)",
                userId, callId, roomManager.PeerCount(callId));

            // Best-effort notify remaining peers that this one is gone.
            // ct may already be cancelled; use CancellationToken.None.
            try
            {
                var leftJson = $"{{\"type\":\"peer_left\",\"from\":\"{userId}\",\"callId\":\"{callId}\"}}";
                var leftBytes = new ArraySegment<byte>(Encoding.UTF8.GetBytes(leftJson));
                await roomManager.BroadcastAsync(callId, userId, leftBytes, WebSocketMessageType.Text, CancellationToken.None);
            }
            catch
            {
                // ignored — peer cleanup is best-effort
            }
        }
    }

    /// <summary>
    /// Reads frames until end-of-message, growing into <paramref name="buffer"/> if needed.
    /// Returns total bytes read and the message type.
    /// </summary>
    private static async Task<(int Count, WebSocketMessageType MessageType)> ReceiveFullMessageAsync(
        WebSocket ws,
        byte[] buffer,
        CancellationToken ct)
    {
        int totalRead = 0;

        while (true)
        {
            var segment = new ArraySegment<byte>(buffer, totalRead, buffer.Length - totalRead);
            var result = await ws.ReceiveAsync(segment, ct);

            totalRead += result.Count;

            if (result.EndOfMessage)
                return (totalRead, result.MessageType);

            // Message spans multiple frames — keep reading (rare for signaling)
            if (totalRead >= buffer.Length)
            {
                // Buffer exhausted without end-of-message; discard and close
                await ws.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message too large", ct);
                return (0, WebSocketMessageType.Close);
            }
        }
    }
}
