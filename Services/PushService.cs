using FirebaseAdmin.Messaging;
using Microsoft.EntityFrameworkCore;
using Workspace.Data;

namespace Workspace.Services;

public sealed class PushService(
    AppDbContext db,
    ILogger<PushService> logger)
{
    // FCM TTL for an incoming-call push: just past the typical "ring duration".
    private static readonly TimeSpan IncomingCallTtl = TimeSpan.FromSeconds(45);

    // Message pushes survive longer so a notification still arrives after the
    // device comes back online, but not so long that stale messages surprise the user.
    private static readonly TimeSpan NewMessageTtl = TimeSpan.FromHours(24);

    /// <summary>
    /// Sends a high-priority data-only FCM message to all of the callee's registered tokens.
    /// Stale tokens are pruned automatically when FCM reports them as unregistered.
    /// Errors are swallowed: a failed push must not break the call-start endpoint.
    /// </summary>
    public async Task SendIncomingCallAsync(
        Guid calleeUserId,
        Guid callId,
        Guid callerUserId,
        string callerDisplayName,
        string callerCode,
        CancellationToken ct = default)
    {
        if (FirebaseAdmin.FirebaseApp.DefaultInstance is null)
        {
            logger.LogWarning("PushService: FirebaseApp not initialised — skipping push for call {CallId}", callId);
            return;
        }

        var tokens = await db.PushTokens
            .AsNoTracking()
            .Where(x => x.UserId == calleeUserId)
            .Select(x => x.Token)
            .ToListAsync(ct);

        if (tokens.Count == 0)
        {
            logger.LogInformation("PushService: callee {UserId} has no registered tokens; skipping push", calleeUserId);
            return;
        }

        var data = new Dictionary<string, string>
        {
            ["type"] = "incoming_call",
            ["callId"] = callId.ToString(),
            ["callerUserId"] = callerUserId.ToString(),
            ["callerName"] = callerDisplayName ?? string.Empty,
            ["callerCode"] = callerCode ?? string.Empty,
            ["sentAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()
        };

        var message = new MulticastMessage
        {
            Tokens = tokens,
            Data = data,
            Android = new AndroidConfig
            {
                Priority = Priority.High,
                TimeToLive = IncomingCallTtl,
                // No Notification block — data-only so the app builds its own CallStyle UI.
            }
        };

        try
        {
            var response = await FirebaseMessaging.DefaultInstance.SendEachForMulticastAsync(message, ct);
            await PruneStaleTokensAsync(tokens, response, ct);

            logger.LogInformation(
                "PushService: incoming-call push for call {CallId} -> callee {UserId}: {Success} success / {Failure} failure",
                callId, calleeUserId, response.SuccessCount, response.FailureCount);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "PushService: failed to send incoming-call push for call {CallId}", callId);
        }
    }

    /// <summary>
    /// Sends a high-priority, data-only FCM push to all of the receiver's tokens for a new
    /// message. The payload carries the sender's name and the E2EE ciphertext only — the
    /// server never sees plaintext, so the client decrypts locally to build the preview.
    /// Errors are swallowed: a failed push must not break the send-message endpoint.
    /// </summary>
    public async Task SendNewMessageAsync(
        Guid receiverUserId,
        Guid senderUserId,
        string senderDisplayName,
        Guid messageId,
        string encryptedMessage,
        string encryptedKeyForReceiver,
        string iv,
        CancellationToken ct = default)
    {
        if (FirebaseAdmin.FirebaseApp.DefaultInstance is null)
        {
            logger.LogWarning("PushService: FirebaseApp not initialised — skipping message push for {MessageId}", messageId);
            return;
        }

        var tokens = await db.PushTokens
            .AsNoTracking()
            .Where(x => x.UserId == receiverUserId)
            .Select(x => x.Token)
            .ToListAsync(ct);

        if (tokens.Count == 0)
        {
            logger.LogInformation("PushService: receiver {UserId} has no registered tokens; skipping message push", receiverUserId);
            return;
        }

        var data = new Dictionary<string, string>
        {
            ["type"] = "new_message",
            // conversationId doubles as the deep-link target: the other party is the sender.
            ["conversationId"] = senderUserId.ToString(),
            ["senderUserId"] = senderUserId.ToString(),
            ["senderName"] = senderDisplayName ?? string.Empty,
            ["messageId"] = messageId.ToString(),
            // E2EE ciphertext for local decryption. No plaintext ever leaves the server.
            ["encryptedMessage"] = encryptedMessage ?? string.Empty,
            ["encryptedKey"] = encryptedKeyForReceiver ?? string.Empty,
            ["iv"] = iv ?? string.Empty,
            ["sentAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()
        };

        var message = new MulticastMessage
        {
            Tokens = tokens,
            Data = data,
            Android = new AndroidConfig
            {
                Priority = Priority.High,
                TimeToLive = NewMessageTtl
                // Data-only (no Notification block): guarantees onMessageReceived fires in the
                // background so the client can decrypt and build the preview itself.
            }
        };

        try
        {
            var response = await FirebaseMessaging.DefaultInstance.SendEachForMulticastAsync(message, ct);
            await PruneStaleTokensAsync(tokens, response, ct);

            logger.LogInformation(
                "PushService: message push {MessageId} -> receiver {UserId}: {Success} success / {Failure} failure",
                messageId, receiverUserId, response.SuccessCount, response.FailureCount);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "PushService: failed to send message push {MessageId}", messageId);
        }
    }

    private async Task PruneStaleTokensAsync(
        List<string> tokens,
        BatchResponse response,
        CancellationToken ct)
    {
        var stale = new List<string>();
        for (var i = 0; i < response.Responses.Count; i++)
        {
            var r = response.Responses[i];
            if (r.IsSuccess) continue;

            var code = r.Exception?.MessagingErrorCode;
            if (code is MessagingErrorCode.Unregistered or MessagingErrorCode.InvalidArgument)
            {
                stale.Add(tokens[i]);
            }
        }

        if (stale.Count == 0) return;

        var deleted = await db.PushTokens
            .Where(x => stale.Contains(x.Token))
            .ExecuteDeleteAsync(ct);

        logger.LogInformation("PushService: pruned {Count} stale FCM tokens", deleted);
    }
}
