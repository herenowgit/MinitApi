using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Workspace.Data;
using Workspace.Domain;
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

        users.MapPut("/{userId:guid}/push-tokens", RegisterPushTokenAsync)
            .WithName("RegisterPushToken")
            .WithSummary("Register or refresh an FCM push token for a user")
            .RequireRateLimiting("public-per-ip");

        users.MapDelete("/{userId:guid}/push-tokens/{token}", DeletePushTokenAsync)
            .WithName("DeletePushToken")
            .WithSummary("Remove an FCM push token (logout / app removal)")
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

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException?.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) == true
           || ex.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase);

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
}
