using Microsoft.EntityFrameworkCore;
using Workspace.Data;
using Workspace.Domain;

namespace Workspace.Services;

/// <summary>
/// Periodically deletes accounts that have been inactive longer than the user's
/// configured <see cref="AutoDeleteAccountMode"/> window. Each account is removed
/// via <see cref="AccountDeletionService"/> in its own transaction, so a single
/// failure never aborts the whole cycle. Mirrors the other cleanup workers.
/// </summary>
public sealed class AccountInactivityCleanupWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<AccountInactivityCleanupWorker> logger) : BackgroundService
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromHours(6);

    private static readonly (AutoDeleteAccountMode Mode, int Days)[] Windows =
    [
        (AutoDeleteAccountMode.FiveDays, 5),
        (AutoDeleteAccountMode.FifteenDays, 15),
        (AutoDeleteAccountMode.ThirtyDays, 30)
        // AutoDeleteAccountMode.Never is intentionally never processed.
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("AccountInactivityCleanupWorker started; cleanup interval is {Interval}", CleanupInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "AccountInactivityCleanupWorker cleanup cycle failed");
            }

            try
            {
                await Task.Delay(CleanupInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        logger.LogInformation("AccountInactivityCleanupWorker stopped");
    }

    private async Task CleanupOnceAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var deletionService = scope.ServiceProvider.GetRequiredService<AccountDeletionService>();
        var nowUtc = DateTime.UtcNow;

        var deleted = 0;
        foreach (var (mode, days) in Windows)
        {
            var cutoffUtc = nowUtc.AddDays(-days);

            // Collect candidate ids first, then delete each in its own transaction.
            var candidateIds = await db.Users
                .Where(u => u.AutoDeleteAccountMode == mode && u.LastActivityAt < cutoffUtc)
                .Select(u => u.Id)
                .ToListAsync(ct);

            foreach (var userId in candidateIds)
            {
                try
                {
                    if (await deletionService.DeleteAccountAsync(userId, ct))
                    {
                        deleted++;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Inactivity deletion failed for user {UserId}; continuing", userId);
                }
            }
        }

        if (deleted > 0)
        {
            logger.LogInformation("AccountInactivityCleanupWorker deleted {DeletedCount} inactive account(s)", deleted);
        }
    }
}
