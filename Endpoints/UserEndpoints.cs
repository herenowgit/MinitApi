using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Workspace.Data;
using Workspace.Domain;
using Workspace.Dtos.Calls;
using Workspace.Dtos.Users;
using Workspace.Services;

namespace Workspace.Endpoints;

public static class UserEndpoints
{
    public static RouteGroupBuilder MapUserEndpoints(this RouteGroupBuilder group)
    {
        var users = group.MapGroup("/users").WithTags("Users");

        users.MapPost("/register", RegisterUserAsync)
            .WithName("RegisterUser")
            .WithSummary("Register a new user with generated code")
            .RequireRateLimiting("public-per-ip");

        users.MapGet("/by-code/{code}", GetByCodeAsync)
            .WithName("GetUserByCode")
            .WithSummary("Find user by invite code")
            .RequireRateLimiting("public-per-ip");

        users.MapPut("/{userId:guid}/public-key", UploadPublicKeyAsync)
            .WithName("UploadPublicKey")
            .WithSummary("Upload or refresh the user's E2EE public key")
            .Produces<PublicKeyResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireRateLimiting("public-per-ip");

        users.MapGet("/{userId:guid}/public-key", GetPublicKeyAsync)
            .WithName("GetPublicKey")
            .WithSummary("Get another user's E2EE public key for message encryption")
            .Produces<PublicKeyResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .RequireRateLimiting("public-per-ip");

        users.MapPost("/auto-delete-setting", UpdateAutoDeleteSettingAsync)
            .WithName("UpdateAutoDeleteCallHistorySetting")
            .WithSummary("Update a user's call history auto-delete setting")
            .Produces<UpdateAutoDeleteCallHistorySettingResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        users.MapGet("/{userId:guid}/auto-delete-setting", GetAutoDeleteSettingAsync)
            .WithName("GetAutoDeleteCallHistorySetting")
            .WithSummary("Get a user's call history auto-delete setting")
            .Produces<AutoDeleteCallHistorySettingResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        users.MapPost("/auto-delete-message-setting", UpdateAutoDeleteMessageSettingAsync)
            .WithName("UpdateAutoDeleteMessageSetting")
            .WithSummary("Update a user's message auto-delete setting")
            .Produces<UpdateAutoDeleteMessageSettingResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        users.MapGet("/{userId:guid}/auto-delete-message-setting", GetAutoDeleteMessageSettingAsync)
            .WithName("GetAutoDeleteMessageSetting")
            .WithSummary("Get a user's message auto-delete setting")
            .Produces<AutoDeleteMessageSettingResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        users.MapPut("/{userId:guid}/push-tokens", RegisterPushTokenAsync)
            .WithName("RegisterPushToken")
            .WithSummary("Register or refresh an FCM push token for a user")
            .RequireRateLimiting("public-per-ip");

        users.MapDelete("/{userId:guid}/push-tokens/{token}", DeletePushTokenAsync)
            .WithName("DeletePushToken")
            .WithSummary("Remove an FCM push token (logout / app removal)")
            .RequireRateLimiting("public-per-ip");

        users.MapGet("/{userId:guid}/calls/recent", GetRecentCallsAsync)
            .WithName("GetRecentCalls")
            .WithSummary("Recent calls (incoming + outgoing) for a user")
            .RequireRateLimiting("public-per-ip");

        return users;
    }

    private static async Task<IResult> RegisterUserAsync(
        [FromBody] RegisterUserRequest request,
        AppDbContext db,
        ICodeGenerator codeGenerator,
        CancellationToken ct)
    {
        var normalizedDisplayName = request.DisplayName.Trim();
        if (string.IsNullOrWhiteSpace(normalizedDisplayName))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["displayName"] = ["DisplayName must not be empty."]
            });
        }

        var user = new User
        {
            Id = Guid.NewGuid(),
            DisplayName = normalizedDisplayName,
            MonthlyLimitSeconds = 6000,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        const int maxAttempts = 8;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            user.Code = codeGenerator.GenerateCode(8);
            db.Users.Add(user);

            try
            {
                await db.SaveChangesAsync(ct);

                var response = new RegisterUserResponse
                {
                    UserId = user.Id,
                    DisplayName = user.DisplayName,
                    Code = user.Code,
                    MonthlyLimitSeconds = user.MonthlyLimitSeconds,
                    CreatedAt = user.CreatedAt
                };

                return Results.Created($"/api/users/{user.Id}", response);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                db.Entry(user).State = EntityState.Detached;
                if (attempt == maxAttempts)
                {
                    return Results.Problem(
                        title: "Failed to generate unique code",
                        detail: "Could not allocate a unique user code. Please retry.",
                        statusCode: StatusCodes.Status500InternalServerError);
                }
            }
        }

        return Results.Problem(
            title: "Unexpected error",
            detail: "Could not register user.",
            statusCode: StatusCodes.Status500InternalServerError);
    }

    private static async Task<IResult> GetByCodeAsync(
        string code,
        AppDbContext db,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length != 8)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["code"] = ["Code must be exactly 8 characters."]
            });
        }

        var normalizedCode = code.Trim().ToUpperInvariant();
        var user = await db.Users
            .AsNoTracking()
            .Where(x => x.Code == normalizedCode && x.IsActive)
            .Select(x => new UserByCodeResponse
            {
                UserId = x.Id,
                DisplayName = x.DisplayName
            })
            .FirstOrDefaultAsync(ct);

        return user is null
            ? Results.Problem(
                title: "User not found",
                detail: "No active user found for the provided code.",
                statusCode: StatusCodes.Status404NotFound,
                type: "user_not_found")
            : Results.Ok(user);
    }

    private static async Task<IResult> UploadPublicKeyAsync(
        Guid userId,
        [FromBody] UploadPublicKeyRequest request,
        AppDbContext db,
        CancellationToken ct)
    {
        var key = request.PublicKey?.Trim() ?? string.Empty;

        // Validate as plausible Base64 of a bounded size. The server never
        // interprets the key material — it only relays it to other clients.
        if (key.Length is < 100 or > 1024 || !IsBase64(key))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["publicKey"] = ["A valid Base64-encoded public key is required."]
            });
        }

        var user = await db.Users.FirstOrDefaultAsync(x => x.Id == userId && x.IsActive, ct);
        if (user is null)
        {
            return UserNotFound(userId);
        }

        user.PublicKey = key;
        user.PublicKeyUpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.Ok(new PublicKeyResponse(user.Id, user.PublicKey));
    }

    private static async Task<IResult> GetPublicKeyAsync(
        Guid userId,
        AppDbContext db,
        CancellationToken ct)
    {
        var result = await db.Users
            .AsNoTracking()
            .Where(x => x.Id == userId && x.IsActive)
            .Select(x => new { x.Id, x.PublicKey })
            .FirstOrDefaultAsync(ct);

        if (result is null)
        {
            return UserNotFound(userId);
        }

        return Results.Ok(new PublicKeyResponse(result.Id, result.PublicKey));
    }

    private static bool IsBase64(string value)
    {
        Span<byte> buffer = new byte[((value.Length * 3) + 3) / 4];
        return Convert.TryFromBase64String(value, buffer, out _);
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException?.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) == true
           || ex.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase);

    private static async Task<IResult> UpdateAutoDeleteSettingAsync(
        [FromBody] UpdateAutoDeleteCallHistorySettingRequest request,
        AppDbContext db,
        CancellationToken ct)
    {
        if (!TryReadAutoDeleteMode(request.Mode, out var mode))
        {
            return InvalidAutoDeleteMode();
        }

        var user = await db.Users.FirstOrDefaultAsync(x => x.Id == request.UserId, ct);
        if (user is null)
        {
            return UserNotFound(request.UserId);
        }

        user.AutoDeleteCallHistoryMode = mode;
        await db.SaveChangesAsync(ct);

        return Results.Ok(new UpdateAutoDeleteCallHistorySettingResponse(true, user.AutoDeleteCallHistoryMode.ToString()));
    }

    private static async Task<IResult> GetAutoDeleteSettingAsync(
        Guid userId,
        AppDbContext db,
        CancellationToken ct)
    {
        var mode = await db.Users
            .AsNoTracking()
            .Where(x => x.Id == userId)
            .Select(x => (AutoDeleteCallHistoryMode?)x.AutoDeleteCallHistoryMode)
            .FirstOrDefaultAsync(ct);

        return mode is null
            ? UserNotFound(userId)
            : Results.Ok(new AutoDeleteCallHistorySettingResponse(mode.Value.ToString()));
    }

    private static bool TryReadAutoDeleteMode(string? value, out AutoDeleteCallHistoryMode mode)
    {
        mode = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        return int.TryParse(normalized, out var numericMode)
            ? TryValidateAutoDeleteMode(numericMode, out mode)
            : Enum.TryParse(normalized, ignoreCase: true, out mode) && Enum.IsDefined(mode);
    }

    private static bool TryValidateAutoDeleteMode(int value, out AutoDeleteCallHistoryMode mode)
    {
        mode = (AutoDeleteCallHistoryMode)value;
        return Enum.IsDefined(mode);
    }

    private static async Task<IResult> UpdateAutoDeleteMessageSettingAsync(
        [FromBody] UpdateAutoDeleteMessageSettingRequest request,
        AppDbContext db,
        CancellationToken ct)
    {
        if (!TryReadAutoDeleteMessageMode(request.Mode, out var mode))
        {
            return InvalidAutoDeleteMode();
        }

        var user = await db.Users.FirstOrDefaultAsync(x => x.Id == request.UserId, ct);
        if (user is null)
        {
            return UserNotFound(request.UserId);
        }

        user.AutoDeleteMessageMode = mode;
        await db.SaveChangesAsync(ct);

        return Results.Ok(new UpdateAutoDeleteMessageSettingResponse(true, user.AutoDeleteMessageMode.ToString()));
    }

    private static async Task<IResult> GetAutoDeleteMessageSettingAsync(
        Guid userId,
        AppDbContext db,
        CancellationToken ct)
    {
        var mode = await db.Users
            .AsNoTracking()
            .Where(x => x.Id == userId)
            .Select(x => (AutoDeleteMessageMode?)x.AutoDeleteMessageMode)
            .FirstOrDefaultAsync(ct);

        return mode is null
            ? UserNotFound(userId)
            : Results.Ok(new AutoDeleteMessageSettingResponse(mode.Value.ToString()));
    }

    private static bool TryReadAutoDeleteMessageMode(string? value, out AutoDeleteMessageMode mode)
    {
        mode = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim();
        return int.TryParse(normalized, out var numericMode)
            ? TryValidateAutoDeleteMessageMode(numericMode, out mode)
            : Enum.TryParse(normalized, ignoreCase: true, out mode) && Enum.IsDefined(mode);
    }

    private static bool TryValidateAutoDeleteMessageMode(int value, out AutoDeleteMessageMode mode)
    {
        mode = (AutoDeleteMessageMode)value;
        return Enum.IsDefined(mode);
    }

    private static IResult InvalidAutoDeleteMode()
        => Results.Problem(
            title: "Invalid auto-delete mode",
            statusCode: StatusCodes.Status400BadRequest);

    private static IResult UserNotFound(Guid userId)
        => Results.Problem(
            title: "User not found",
            detail: $"User '{userId}' does not exist.",
            statusCode: StatusCodes.Status404NotFound,
            type: "user_not_found");

    private static async Task<IResult> RegisterPushTokenAsync(
        Guid userId,
        [FromBody] RegisterPushTokenRequest request,
        AppDbContext db,
        CancellationToken ct)
    {
        var token = request.Token?.Trim() ?? string.Empty;
        if (token.Length < 8)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["token"] = ["Token must be at least 8 characters."]
            });
        }

        var userExists = await db.Users.AnyAsync(x => x.Id == userId && x.IsActive, ct);
        if (!userExists)
        {
            return Results.Problem(
                title: "User not found",
                detail: $"User '{userId}' does not exist or is inactive.",
                statusCode: StatusCodes.Status404NotFound,
                type: "user_not_found");
        }

        var platform = string.IsNullOrWhiteSpace(request.Platform) ? "android" : request.Platform.Trim().ToLowerInvariant();
        var deviceId = string.IsNullOrWhiteSpace(request.DeviceId) ? null : request.DeviceId.Trim();
        var nowUtc = DateTime.UtcNow;

        // Tokens are globally unique to a device. If this token is currently
        // registered to a different user (account switch on the same device),
        // detach it first so the user index doesn't double-count it.
        var others = await db.PushTokens
            .Where(x => x.Token == token && x.UserId != userId)
            .ToListAsync(ct);
        if (others.Count > 0)
        {
            db.PushTokens.RemoveRange(others);
        }

        var existing = await db.PushTokens
            .FirstOrDefaultAsync(x => x.UserId == userId && x.Token == token, ct);

        PushToken entity;
        if (existing is null)
        {
            entity = new PushToken
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Token = token,
                DeviceId = deviceId,
                Platform = platform,
                CreatedAt = nowUtc,
                LastSeenAt = nowUtc
            };
            db.PushTokens.Add(entity);
        }
        else
        {
            existing.DeviceId = deviceId ?? existing.DeviceId;
            existing.Platform = platform;
            existing.LastSeenAt = nowUtc;
            entity = existing;
        }

        await db.SaveChangesAsync(ct);

        return Results.Ok(new PushTokenResponse(entity.Id, entity.UserId, entity.Platform, entity.LastSeenAt));
    }

    private static async Task<IResult> DeletePushTokenAsync(
        Guid userId,
        string token,
        AppDbContext db,
        CancellationToken ct)
    {
        var normalized = token?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            return Results.NoContent();
        }

        var rows = await db.PushTokens
            .Where(x => x.UserId == userId && x.Token == normalized)
            .ExecuteDeleteAsync(ct);

        return rows > 0 ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> GetRecentCallsAsync(
        Guid userId,
        AppDbContext db,
        CancellationToken ct,
        [FromQuery] int limit = 50)
    {
        if (limit is < 1 or > 200) limit = 50;

        var userExists = await db.Users.AnyAsync(x => x.Id == userId, ct);
        if (!userExists)
        {
            return Results.Problem(
                title: "User not found",
                detail: $"User '{userId}' does not exist.",
                statusCode: StatusCodes.Status404NotFound,
                type: "user_not_found");
        }

        // A call is relevant to this user if they either created it OR appear
        // in its participants table. Pull the IDs in one query, then load the
        // full sessions + participants + counterpart user info in a second
        // pass keyed by those IDs.
        var relevantCallIds = await (
            from c in db.CallSessions
            where c.CreatedByUserId == userId
                  || db.CallParticipants.Any(p => p.CallSessionId == c.Id && p.UserId == userId)
            orderby c.CreatedAt descending
            select c.Id
        ).Take(limit).ToListAsync(ct);

        if (relevantCallIds.Count == 0)
        {
            return Results.Ok(Array.Empty<RecentCallResponse>());
        }

        var sessions = await db.CallSessions
            .Where(c => relevantCallIds.Contains(c.Id))
            .Include(c => c.Participants)
            .ToListAsync(ct);

        // For 1:1 calls the "other party" is whichever side isn't `userId`.
        // For outgoing calls we use the persisted CalleeUserId so even
        // never-answered calls show the correct counterpart name.
        static Guid OtherPartyId(CallSession c, Guid me)
        {
            if (c.CreatedByUserId == me)
            {
                return c.CalleeUserId
                    ?? c.Participants.FirstOrDefault(p => p.UserId != me)?.UserId
                    ?? me;
            }
            return c.CreatedByUserId;
        }

        var otherUserIds = sessions.Select(c => OtherPartyId(c, userId)).Distinct().ToList();

        var users = await db.Users
            .AsNoTracking()
            .Where(u => otherUserIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, ct);

        var ordered = sessions.OrderByDescending(c => c.CreatedAt).ToList();
        var result = ordered.Select(c =>
        {
            var otherUserId = OtherPartyId(c, userId);
            users.TryGetValue(otherUserId, out var other);
            var billed = c.Participants.FirstOrDefault(p => p.UserId == c.CreatedByUserId)?.BilledSeconds ?? 0;

            return new RecentCallResponse(
                CallId: c.Id,
                Direction: c.CreatedByUserId == userId ? "outgoing" : "incoming",
                Status: c.Status.ToString(),
                OtherUserId: otherUserId,
                OtherDisplayName: other?.DisplayName ?? string.Empty,
                OtherCode: other?.Code ?? string.Empty,
                CreatedAt: c.CreatedAt,
                StartedAt: c.StartedAt,
                EndedAt: c.EndedAt,
                BilledSeconds: billed);
        }).ToList();

        return Results.Ok(result);
    }
}