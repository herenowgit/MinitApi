using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Workspace.Data;
using Workspace.Domain;
using Workspace.Dtos.Contacts;
using Workspace.Dtos.Invites;

namespace Workspace.Services;

public sealed class InviteService(AppDbContext dbContext, IConfiguration configuration)
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int DefaultCodeLength = 10;
    private const int DefaultTtlMinutes = 10;
    private const int DefaultMaxRedemptions = 1;
    private const int MaxGenerateAttempts = 8;

    public string GenerateInviteCode()
    {
        Span<char> chars = stackalloc char[DefaultCodeLength];
        Span<byte> bytes = stackalloc byte[DefaultCodeLength];
        RandomNumberGenerator.Fill(bytes);

        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = Alphabet[bytes[i] % Alphabet.Length];
        }

        return new string(chars);
    }

    public string HashInviteCode__(string inviteCode)
    {
        var normalized = NormalizeInviteCode(inviteCode);
        var pepper = configuration["Security:InviteCodePepper"];
        if (string.IsNullOrWhiteSpace(pepper))
        {
            throw new InvalidOperationException("Security:InviteCodePepper must be configured.");
        }

        var input = Encoding.UTF8.GetBytes(normalized + pepper);
        var hash = SHA256.HashData(input);
        return Convert.ToHexString(hash);
    }

    public async Task<CreateInviteResult> CreateInviteAsync(CreateInviteRequest request, CancellationToken ct = default)
    {
        if (request.OwnerUserId == Guid.Empty)
        {
            return CreateInviteResult.Invalid("owner_required", "ownerUserId is required.");
        }

        var ownerExists = await dbContext.Users
            .AsNoTracking()
            .AnyAsync(x => x.Id == request.OwnerUserId && x.IsActive, ct);
        if (!ownerExists)
        {
            return CreateInviteResult.Invalid("user_not_found", "Owner user does not exist or is inactive.");
        }

        var ttlMinutes = request.TtlMinutes.GetValueOrDefault(DefaultTtlMinutes);
        var maxRedemptions = request.MaxRedemptions.GetValueOrDefault(DefaultMaxRedemptions);
        if (ttlMinutes is < 1 or > 1440)
        {
            return CreateInviteResult.Invalid("invalid_ttl", "ttlMinutes must be between 1 and 1440.");
        }

        if (maxRedemptions is < 1 or > 100)
        {
            return CreateInviteResult.Invalid("invalid_max_redemptions", "maxRedemptions must be between 1 and 100.");
        }

        var nowUtc = DateTimeOffset.UtcNow;
        for (var attempt = 1; attempt <= MaxGenerateAttempts; attempt++)
        {
            var code = GenerateInviteCode();
            var invite = new OneTimeInviteCode
            {
                Id = Guid.NewGuid(),
                OwnerUserId = request.OwnerUserId,
                TokenHash = code,// HashInviteCode(code),
                CreatedAtUtc = nowUtc,
                ExpiresAtUtc = nowUtc.AddMinutes(ttlMinutes),
                MaxRedemptions = maxRedemptions,
                RedemptionCount = 0
            };

            dbContext.OneTimeInviteCodes.Add(invite);
            try
            {
                await dbContext.SaveChangesAsync(ct);
                return CreateInviteResult.Created(code, invite.ExpiresAtUtc);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                dbContext.Entry(invite).State = EntityState.Detached;
            }
        }

        return CreateInviteResult.Invalid("invite_generation_failed", "Could not generate a unique invite code.");
    }

    public InviteValidationResult ValidateInvite(OneTimeInviteCode? invite, Guid redeemerUserId, DateTimeOffset nowUtc)
    {
        if (invite is null)
        {
            return InviteValidationResult.Invalid("invite_invalid", "Invite code is invalid.", StatusCodes.Status404NotFound);
        }

        if (invite.ExpiresAtUtc <= nowUtc)
        {
            return InviteValidationResult.Invalid("invite_expired", "Invite code has expired.", StatusCodes.Status410Gone);
        }

        if (invite.RevokedAtUtc is not null)
        {
            return InviteValidationResult.Invalid("invite_revoked", "Invite code has been revoked.", StatusCodes.Status410Gone);
        }

        if (invite.MaxRedemptions == 1 && invite.UsedAtUtc is not null)
        {
            return InviteValidationResult.Invalid("invite_used", "Invite code has already been used.", StatusCodes.Status409Conflict);
        }

        if (invite.RedemptionCount >= invite.MaxRedemptions)
        {
            return InviteValidationResult.Invalid("invite_used", "Invite code redemption limit has been reached.", StatusCodes.Status409Conflict);
        }

        if (invite.OwnerUserId == redeemerUserId)
        {
            return InviteValidationResult.Invalid("self_invite", "Users cannot redeem their own invite code.", StatusCodes.Status400BadRequest);
        }

        return InviteValidationResult.Valid();
    }

    public async Task<RedeemInviteResult> RedeemInviteAsync(RedeemInviteRequest request, CancellationToken ct = default)
    {
        if (request.RedeemerUserId == Guid.Empty)
        {
            return RedeemInviteResult.Invalid("redeemer_required", "redeemerUserId is required.", StatusCodes.Status400BadRequest);
        }

        string tokenHash;
        try
        {
            tokenHash = request.InviteCode;// HashInviteCode();
        }
        catch (ArgumentException)
        {
            return RedeemInviteResult.Invalid("invite_invalid", "Invite code is invalid.", StatusCodes.Status400BadRequest);
        }

        await using var tx = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var nowUtc = DateTimeOffset.UtcNow;
            var invite = await dbContext.OneTimeInviteCodes
                .FirstOrDefaultAsync(x => x.TokenHash == tokenHash, ct);

            var validation = ValidateInvite(invite, request.RedeemerUserId, nowUtc);
            if (!validation.Success)
            {
                await tx.RollbackAsync(ct);
                return RedeemInviteResult.Invalid(validation.Code, validation.Detail, validation.StatusCode);
            }

            var users = await dbContext.Users
                .Where(x => x.Id == invite!.OwnerUserId || x.Id == request.RedeemerUserId)
                .ToListAsync(ct);

            var owner = users.FirstOrDefault(x => x.Id == invite!.OwnerUserId && x.IsActive);
            if (owner is null)
            {
                await tx.RollbackAsync(ct);
                return RedeemInviteResult.Invalid("user_not_found", "Invite owner does not exist or is inactive.", StatusCodes.Status404NotFound);
            }

            var redeemer = users.FirstOrDefault(x => x.Id == request.RedeemerUserId && x.IsActive);
            if (redeemer is null)
            {
                await tx.RollbackAsync(ct);
                return RedeemInviteResult.Invalid("user_not_found", "Redeemer user does not exist or is inactive.", StatusCodes.Status404NotFound);
            }

            var existingContacts = await dbContext.Contacts
                .Where(x =>
                    (x.OwnerUserId == invite!.OwnerUserId && x.ContactUserId == request.RedeemerUserId) ||
                    (x.OwnerUserId == request.RedeemerUserId && x.ContactUserId == invite!.OwnerUserId))
                .ToListAsync(ct);

            var nowDateTime = nowUtc.UtcDateTime;
            if (!existingContacts.Any(x => x.OwnerUserId == invite!.OwnerUserId && x.ContactUserId == request.RedeemerUserId))
            {
                dbContext.Contacts.Add(new Contact
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = invite!.OwnerUserId,
                    ContactUserId = request.RedeemerUserId,
                    CreatedAt = nowDateTime
                });
            }

            if (!existingContacts.Any(x => x.OwnerUserId == request.RedeemerUserId && x.ContactUserId == invite!.OwnerUserId))
            {
                dbContext.Contacts.Add(new Contact
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = request.RedeemerUserId,
                    ContactUserId = invite!.OwnerUserId,
                    CreatedAt = nowDateTime
                });
            }

            invite!.RedemptionCount++;
            if (invite.MaxRedemptions == 1)
            {
                invite.UsedAtUtc = nowUtc;
                invite.UsedByUserId = request.RedeemerUserId;
            }

            await dbContext.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return RedeemInviteResult.Redeemed(new ContactItemResponse(
                owner.Id,
                owner.DisplayName,
                owner.Code,
                nowDateTime));
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<RevokeInviteResult> RevokeInviteAsync(Guid inviteId, Guid ownerUserId, CancellationToken ct = default)
    {
        if (inviteId == Guid.Empty || ownerUserId == Guid.Empty)
        {
            return RevokeInviteResult.Invalid("invite_invalid", "Invite id and ownerUserId are required.", StatusCodes.Status400BadRequest);
        }

        var invite = await dbContext.OneTimeInviteCodes.FirstOrDefaultAsync(x => x.Id == inviteId, ct);
        if (invite is null)
        {
            return RevokeInviteResult.Invalid("invite_invalid", "Invite does not exist.", StatusCodes.Status404NotFound);
        }

        if (invite.OwnerUserId != ownerUserId)
        {
            return RevokeInviteResult.Invalid("forbidden", "Only the invite owner can revoke this invite.", StatusCodes.Status403Forbidden);
        }

        invite.RevokedAtUtc ??= DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);

        return RevokeInviteResult.Revoked();
    }

    public async Task<IReadOnlyList<ActiveInviteResponse>> ListActiveInvitesAsync(Guid ownerUserId, CancellationToken ct = default)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var invites = await dbContext.OneTimeInviteCodes
            .AsNoTracking()
            .Where(x =>
                x.OwnerUserId == ownerUserId &&
                x.RevokedAtUtc == null)
            .ToListAsync(ct);

        return invites
            .Where(x => x.ExpiresAtUtc > nowUtc && x.RedemptionCount < x.MaxRedemptions)
            .OrderBy(x => x.ExpiresAtUtc)
            .Select(x => new ActiveInviteResponse(
                x.TokenHash,
                x.ExpiresAtUtc,
                x.CreatedAtUtc,
                x.MaxRedemptions,
                x.RedemptionCount))
            .ToList();
    }

    private static string NormalizeInviteCode(string inviteCode)
    {
        var normalized = (inviteCode ?? string.Empty).Replace("-", string.Empty, StringComparison.Ordinal).Trim().ToUpperInvariant();
        if (normalized.Length is < 10 or > 12 || normalized.Any(c => !Alphabet.Contains(c)))
        {
            throw new ArgumentException("Invalid invite code.", nameof(inviteCode));
        }

        return normalized;
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException?.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) == true
           || ex.InnerException?.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) == true
           || ex.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase);
}

public sealed record CreateInviteResult(
    bool Success,
    string? InviteCode,
    DateTimeOffset? ExpiresAtUtc,
    string? Code,
    string? Detail)
{
    public static CreateInviteResult Created(string inviteCode, DateTimeOffset expiresAtUtc)
        => new(true, inviteCode, expiresAtUtc, null, null);

    public static CreateInviteResult Invalid(string code, string detail)
        => new(false, null, null, code, detail);
}

public sealed record InviteValidationResult(bool Success, string Code, string Detail, int StatusCode)
{
    public static InviteValidationResult Valid()
        => new(true, string.Empty, string.Empty, StatusCodes.Status200OK);

    public static InviteValidationResult Invalid(string code, string detail, int statusCode)
        => new(false, code, detail, statusCode);
}

public sealed record RedeemInviteResult(
    bool Success,
    ContactItemResponse? Contact,
    string? Code,
    string? Detail,
    int StatusCode)
{
    public static RedeemInviteResult Redeemed(ContactItemResponse contact)
        => new(true, contact, null, null, StatusCodes.Status200OK);

    public static RedeemInviteResult Invalid(string code, string detail, int statusCode)
        => new(false, null, code, detail, statusCode);
}

public sealed record RevokeInviteResult(bool Success, string? Code, string? Detail, int StatusCode)
{
    public static RevokeInviteResult Revoked()
        => new(true, null, null, StatusCodes.Status200OK);

    public static RevokeInviteResult Invalid(string code, string detail, int statusCode)
        => new(false, code, detail, statusCode);
}
