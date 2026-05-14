using System.Net.WebSockets;

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

        // ── Upgrade to WebSocket ──────────────────────────────────────────────
        using var ws = await context.WebSockets.AcceptWebSocketAsync();
        roomManager.Join(callId, userId, ws);
        logger.LogInformation("Signaling: peer {UserId} joined room {CallId} ({PeerCount} peers)",
            userId, callId, roomManager.PeerCount(callId));

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
