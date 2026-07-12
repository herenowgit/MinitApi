using Microsoft.EntityFrameworkCore;
using Workspace.Data;

namespace Workspace.Services;

/// <summary>
/// Permanently and irreversibly deletes a user account and every record related
/// to it, in one transaction. Used by both the manual "Delete Account" endpoint
/// and the automatic inactivity worker.
///
/// The <c>users</c> table is referenced by several <c>OnDelete(Restrict)</c> FKs
/// (messages on both sides, others' contacts, call participants, created call
/// sessions, invite codes used by the user), so a plain user delete would throw.
/// We therefore remove every dependent set explicitly, children first, rather
/// than relying on cascade — which also makes the behavior identical on the
/// SQLite provider used by the tests and PostgreSQL in production.
/// </summary>
public sealed class AccountDeletionService(AppDbContext db, ILogger<AccountDeletionService> logger)
{
    /// <summary>
    /// Deletes the user and all associated data. Returns <c>false</c> if the user
    /// does not exist (idempotent). Throws (after rollback) on any failure so no
    /// partial/orphaned state is ever committed.
    /// </summary>
    public async Task<bool> DeleteAccountAsync(Guid userId, CancellationToken ct)
    {
        if (!await db.Users.AnyAsync(u => u.Id == userId, ct))
        {
            return false;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // 1. Restrict FKs — must go before the user row.
            await db.Messages
                .Where(m => m.SenderUserId == userId || m.ReceiverUserId == userId)
                .ExecuteDeleteAsync(ct);

            await db.Contacts
                .Where(c => c.OwnerUserId == userId || c.ContactUserId == userId)
                .ExecuteDeleteAsync(ct);

            // This user's participation rows in anyone's calls (Restrict).
            await db.CallParticipants
                .Where(p => p.UserId == userId)
                .ExecuteDeleteAsync(ct);

            // Calls this user created (Restrict); cascades their remaining participants.
            await db.CallSessions
                .Where(s => s.CreatedByUserId == userId)
                .ExecuteDeleteAsync(ct);

            // Calls where this user was the callee are kept, but the now-dangling
            // callee id (no FK) is cleared so no record points at a deleted user.
            await db.CallSessions
                .Where(s => s.CalleeUserId == userId)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.CalleeUserId, (Guid?)null), ct);

            await db.OneTimeInviteCodes
                .Where(i => i.OwnerUserId == userId || i.UsedByUserId == userId)
                .ExecuteDeleteAsync(ct);

            // 2. Cascade-eligible sets — deleted explicitly for provider independence.
            await db.PushTokens.Where(t => t.UserId == userId).ExecuteDeleteAsync(ct);
            await db.MonthlyUsages.Where(u => u.UserId == userId).ExecuteDeleteAsync(ct);
            await db.UsageAdjustments.Where(a => a.UserId == userId).ExecuteDeleteAsync(ct);
            await db.CallHistory.Where(h => h.UserId == userId).ExecuteDeleteAsync(ct);

            // 3. Finally the user.
            await db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync(ct);

            await tx.CommitAsync(ct);
            logger.LogInformation("Deleted account {UserId} and all related data", userId);
            return true;
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(ct);
            logger.LogError(ex, "Account deletion failed for {UserId}; transaction rolled back", userId);
            throw;
        }
    }
}
