using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Workspace.Data;
using Workspace.Domain;
using Workspace.Dtos.Messages;
using Workspace.Services;

namespace Workspace.Endpoints;

public static class MessageEndpoints
{
    private const int MaxEncryptedMessageLength = 4000;
    private const int MaxEncryptedKeyLength = 1024;
    private const int MaxIvLength = 32;
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 100;

    public static RouteGroupBuilder MapMessageEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/messages").WithTags("Messages");

        group.MapPost("/send", SendMessageAsync)
            .WithName("SendMessage")
            .WithSummary("Send a message to another user")
            .Produces<MessageResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireRateLimiting("public-per-ip");

        group.MapGet("/conversation", GetConversationAsync)
            .WithName("GetMessageConversation")
            .WithSummary("Get paged messages between two users")
            .Produces<List<MessageResponse>>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireRateLimiting("public-per-ip");

        group.MapGet("/conversations/{userId:guid}", GetRecentConversationsAsync)
            .WithName("GetRecentMessageConversations")
            .WithSummary("Get latest message conversations for a user")
            .Produces<List<RecentConversationResponse>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireRateLimiting("public-per-ip");

        group.MapPost("/delete-chat", DeleteChatAsync)
            .WithName("DeleteChat")
            .WithSummary("Hard-delete messages in a conversation (mine or all)")
            .Produces<DeleteChatResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireRateLimiting("public-per-ip");

        return group;
    }

    private static async Task<IResult> SendMessageAsync(
        [FromBody] SendMessageRequest request,
        AppDbContext db,
        IServiceScopeFactory scopeFactory,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        var validationErrors = ValidateSendRequest(request);
        if (validationErrors.Count > 0)
        {
            return Results.ValidationProblem(validationErrors);
        }

        var users = await db.Users
            .AsNoTracking()
            .Where(x => x.Id == request.SenderUserId || x.Id == request.ReceiverUserId)
            .Select(x => new { x.Id, x.IsActive, x.DisplayName })
            .ToListAsync(ct);

        var sender = users.FirstOrDefault(x => x.Id == request.SenderUserId && x.IsActive);
        if (sender is null)
        {
            return UserNotFound("Sender not found", request.SenderUserId);
        }

        var receiverExists = users.Any(x => x.Id == request.ReceiverUserId && x.IsActive);
        if (!receiverExists)
        {
            return UserNotFound("Receiver not found", request.ReceiverUserId);
        }

        var message = new Message
        {
            Id = Guid.NewGuid(),
            SenderUserId = request.SenderUserId,
            ReceiverUserId = request.ReceiverUserId,
            EncryptedMessage = request.EncryptedMessage,
            EncryptedKey = request.EncryptedKey,
            EncryptedKeyForSender = request.EncryptedKeyForSender,
            Iv = request.Iv,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            IsDeleted = false
        };

        db.Messages.Add(message);
        await db.SaveChangesAsync(ct);

        // Fire-and-forget the new-message push so the response isn't blocked by FCM.
        // Uses its own DI scope because the request scope (and its AppDbContext) is
        // disposed as soon as we return. The push carries only the sender's name and
        // the E2EE ciphertext — never plaintext — so the client decrypts locally.
        var receiverUserId = request.ReceiverUserId;
        var senderUserId = sender.Id;
        var senderDisplayName = sender.DisplayName;
        var messageId = message.Id;
        var encryptedMessage = message.EncryptedMessage;
        var encryptedKey = message.EncryptedKey;
        var iv = message.Iv;
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var push = scope.ServiceProvider.GetRequiredService<PushService>();
                await push.SendNewMessageAsync(
                    receiverUserId: receiverUserId,
                    senderUserId: senderUserId,
                    senderDisplayName: senderDisplayName,
                    messageId: messageId,
                    encryptedMessage: encryptedMessage,
                    encryptedKeyForReceiver: encryptedKey,
                    iv: iv,
                    ct: CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Background message push for message {MessageId} failed", messageId);
            }
        }, CancellationToken.None);

        var response = ToResponse(message);
        return Results.Created($"/api/messages/{message.Id}", response);
    }

    private static async Task<IResult> GetConversationAsync(
        AppDbContext db,
        CancellationToken ct,
        [FromQuery] Guid userId,
        [FromQuery] Guid otherUserId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize)
    {
        if (userId == Guid.Empty || otherUserId == Guid.Empty)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["userIds"] = ["userId and otherUserId are required."]
            });
        }

        if (userId == otherUserId)
        {
            return Results.Problem(
                title: "Invalid conversation request",
                detail: "userId and otherUserId must be different users.",
                statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?> { ["code"] = "invalid_conversation_participants" });
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var skip = (page - 1) * pageSize;

        var existingUserCount = await db.Users
            .AsNoTracking()
            .Where(x => (x.Id == userId || x.Id == otherUserId) && x.IsActive)
            .CountAsync(ct);

        if (existingUserCount < 2)
        {
            return Results.Problem(
                title: "User not found",
                detail: "One or both conversation users do not exist or are inactive.",
                statusCode: StatusCodes.Status404NotFound,
                extensions: new Dictionary<string, object?> { ["code"] = "user_not_found" });
        }

        var query = db.Messages
            .AsNoTracking()
            .Where(x => !x.IsDeleted
                && ((x.SenderUserId == userId && x.ReceiverUserId == otherUserId)
                    || (x.SenderUserId == otherUserId && x.ReceiverUserId == userId)));

        var messages = IsSqlite(db)
            ? (await query.ToListAsync(ct))
                .OrderByDescending(x => x.CreatedAtUtc)
                .ThenByDescending(x => x.Id)
                .Skip(skip)
                .Take(pageSize)
                .Select(ToResponse)
                .ToList()
            : await query
                .OrderByDescending(x => x.CreatedAtUtc)
                .ThenByDescending(x => x.Id)
                .Skip(skip)
                .Take(pageSize)
                .Select(x => new MessageResponse(
                    x.Id,
                    x.SenderUserId,
                    x.ReceiverUserId,
                    x.EncryptedMessage,
                    x.EncryptedKey,
                    x.EncryptedKeyForSender,
                    x.Iv,
                    x.CreatedAtUtc))
                .ToListAsync(ct);

        return Results.Ok(messages);
    }

    private static async Task<IResult> GetRecentConversationsAsync(
        Guid userId,
        AppDbContext db,
        CancellationToken ct,
        [FromQuery] int limit = 50)
    {
        limit = Math.Clamp(limit, 1, 100);

        var userExists = await db.Users
            .AsNoTracking()
            .AnyAsync(x => x.Id == userId && x.IsActive, ct);

        if (!userExists)
        {
            return UserNotFound("User not found", userId);
        }

        if (IsSqlite(db))
        {
            var sqliteMessages = await db.Messages
                .AsNoTracking()
                .Where(x => !x.IsDeleted && (x.SenderUserId == userId || x.ReceiverUserId == userId))
                .ToListAsync(ct);

            var sqliteConversations = sqliteMessages
                .GroupBy(x => x.SenderUserId == userId ? x.ReceiverUserId : x.SenderUserId)
                .Select(x => x
                    .OrderByDescending(message => message.CreatedAtUtc)
                    .ThenByDescending(message => message.Id)
                    .First())
                .OrderByDescending(x => x.CreatedAtUtc)
                .ThenByDescending(x => x.Id)
                .Take(limit)
                .Select(x => new RecentConversationResponse(
                    x.SenderUserId == userId ? x.ReceiverUserId : x.SenderUserId,
                    ToResponse(x)))
                .ToList();

            return Results.Ok(sqliteConversations);
        }

        var latestByConversation = await db.Messages
            .AsNoTracking()
            .Where(x => !x.IsDeleted && (x.SenderUserId == userId || x.ReceiverUserId == userId))
            .GroupBy(x => x.SenderUserId == userId ? x.ReceiverUserId : x.SenderUserId)
            .Select(x => new
            {
                OtherUserId = x.Key,
                LastCreatedAtUtc = x.Max(message => message.CreatedAtUtc)
            })
            .OrderByDescending(x => x.LastCreatedAtUtc)
            .Take(limit)
            .ToListAsync(ct);

        if (latestByConversation.Count == 0)
        {
            return Results.Ok(Array.Empty<RecentConversationResponse>());
        }

        var otherUserIds = latestByConversation.Select(x => x.OtherUserId).ToList();
        var latestMessages = await db.Messages
            .AsNoTracking()
            .Where(x => !x.IsDeleted
                && ((x.SenderUserId == userId && otherUserIds.Contains(x.ReceiverUserId))
                    || (x.ReceiverUserId == userId && otherUserIds.Contains(x.SenderUserId))))
            .OrderByDescending(x => x.CreatedAtUtc)
            .ThenByDescending(x => x.Id)
            .ToListAsync(ct);

        var conversations = latestByConversation
            .Select(conversation =>
            {
                var message = latestMessages.First(x =>
                    x.CreatedAtUtc == conversation.LastCreatedAtUtc
                    && ((x.SenderUserId == userId && x.ReceiverUserId == conversation.OtherUserId)
                        || (x.ReceiverUserId == userId && x.SenderUserId == conversation.OtherUserId)));

                return new RecentConversationResponse(
                    conversation.OtherUserId,
                    ToResponse(message));
            })
            .ToList();

        return Results.Ok(conversations);
    }

    private static async Task<IResult> DeleteChatAsync(
        [FromBody] DeleteChatRequest request,
        AppDbContext db,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();

        if (request.UserId == Guid.Empty)
        {
            errors["userId"] = ["userId is required."];
        }

        if (request.OtherUserId == Guid.Empty)
        {
            errors["otherUserId"] = ["otherUserId is required."];
        }

        if (request.UserId != Guid.Empty && request.UserId == request.OtherUserId)
        {
            errors["otherUserId"] = ["userId and otherUserId must be different users."];
        }

        if (!Enum.TryParse<DeleteChatMode>(request.Mode?.Trim(), ignoreCase: true, out var mode)
            || !Enum.IsDefined(mode))
        {
            errors["mode"] = ["mode must be 'DeleteMine' or 'DeleteAll'."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var users = await db.Users
            .AsNoTracking()
            .Where(x => x.Id == request.UserId || x.Id == request.OtherUserId)
            .Select(x => new { x.Id, x.IsActive })
            .ToListAsync(ct);

        if (!users.Any(x => x.Id == request.UserId && x.IsActive))
        {
            return UserNotFound("User not found", request.UserId);
        }

        if (!users.Any(x => x.Id == request.OtherUserId && x.IsActive))
        {
            return UserNotFound("Other user not found", request.OtherUserId);
        }

        // Deletion is always scoped to the two-user conversation, so a caller can
        // never remove messages outside it. ExecuteDeleteAsync issues a single
        // batched SQL DELETE (no rows loaded into memory) served by the existing
        // (SenderUserId, ReceiverUserId, CreatedAtUtc) index.
        int deleted;
        if (mode == DeleteChatMode.DeleteMine)
        {
            // "My messages in this conversation" are exactly those I sent to the
            // other user — sender = me AND receiver = them.
            deleted = await db.Messages
                .Where(x => x.SenderUserId == request.UserId && x.ReceiverUserId == request.OtherUserId)
                .ExecuteDeleteAsync(ct);
        }
        else
        {
            deleted = await db.Messages
                .Where(x =>
                    (x.SenderUserId == request.UserId && x.ReceiverUserId == request.OtherUserId)
                    || (x.SenderUserId == request.OtherUserId && x.ReceiverUserId == request.UserId))
                .ExecuteDeleteAsync(ct);
        }

        // Audit trail — identifiers and counts only, never message content.
        logger.LogInformation(
            "DeleteChat: user {UserId} deleted {Count} messages with {OtherUserId} (mode {Mode})",
            request.UserId, deleted, request.OtherUserId, mode);

        return Results.Ok(new DeleteChatResponse(deleted, mode.ToString()));
    }

    private static Dictionary<string, string[]> ValidateSendRequest(SendMessageRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (request.SenderUserId == Guid.Empty)
        {
            errors["senderUserId"] = ["senderUserId is required."];
        }

        if (request.ReceiverUserId == Guid.Empty)
        {
            errors["receiverUserId"] = ["receiverUserId is required."];
        }

        if (request.SenderUserId != Guid.Empty && request.SenderUserId == request.ReceiverUserId)
        {
            errors["receiverUserId"] = ["senderUserId and receiverUserId must be different users."];
        }

        // The server never reads message content — it only enforces that the
        // E2EE ciphertext fields are present and within sane size bounds.
        ValidateCiphertextField(errors, "encryptedMessage", request.EncryptedMessage, MaxEncryptedMessageLength);
        ValidateCiphertextField(errors, "encryptedKey", request.EncryptedKey, MaxEncryptedKeyLength);
        ValidateCiphertextField(errors, "encryptedKeyForSender", request.EncryptedKeyForSender, MaxEncryptedKeyLength);
        ValidateCiphertextField(errors, "iv", request.Iv, MaxIvLength);

        return errors;
    }

    private static void ValidateCiphertextField(
        Dictionary<string, string[]> errors,
        string field,
        string value,
        int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors[field] = [$"{field} must not be empty."];
        }
        else if (value.Length > maxLength)
        {
            errors[field] = [$"{field} must be {maxLength} characters or fewer."];
        }
    }

    private static MessageResponse ToResponse(Message message)
        => new(
            message.Id,
            message.SenderUserId,
            message.ReceiverUserId,
            message.EncryptedMessage,
            message.EncryptedKey,
            message.EncryptedKeyForSender,
            message.Iv,
            message.CreatedAtUtc);

    private static bool IsSqlite(AppDbContext db)
        => db.Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite";

    private static IResult UserNotFound(string title, Guid userId)
        => Results.Problem(
            title: title,
            detail: $"User '{userId}' does not exist or is inactive.",
            statusCode: StatusCodes.Status404NotFound,
            extensions: new Dictionary<string, object?> { ["code"] = "user_not_found" });
}
