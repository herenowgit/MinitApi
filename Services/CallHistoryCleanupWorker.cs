using Microsoft.EntityFrameworkCore;
using Workspace.Data;
using Workspace.Domain;

namespace Workspace.Services;

public sealed class CallHistoryCleanupWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<CallHistoryCleanupWorker> logger) : BackgroundService
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan HardDeleteAfter = TimeSpan.FromDays(7);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("CallHistoryCleanupWorker started; cleanup interval is {Interval}", CleanupInterval);

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
                logger.LogError(ex, "CallHistoryCleanupWorker cleanup cycle failed");
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

        logger.LogInformation("CallHistoryCleanupWorker stopped");
    }

    private async Task CleanupOnceAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var nowUtc = DateTimeOffset.UtcNow;

        logger.LogInformation("CallHistoryCleanupWorker cleanup started at {NowUtc}", nowUtc);

        var softDeleted = 0;
        softDeleted += await SoftDeleteForModeAsync(
            db,
            AutoDeleteCallHistoryMode.OneHour,
            nowUtc.AddHours(-1),
            nowUtc,
            ct);

        softDeleted += await SoftDeleteForModeAsync(
            db,
            AutoDeleteCallHistoryMode.SixHours,
            nowUtc.AddHours(-6),
            nowUtc,
            ct);

        // EndOfDay uses UTC midnight as the simplified boundary. Anything
        // before today's UTC date is old enough to hide from call history.
        softDeleted += await SoftDeleteForModeAsync(
            db,
            AutoDeleteCallHistoryMode.EndOfDay,
            new DateTimeOffset(nowUtc.UtcDateTime.Date, TimeSpan.Zero),
            nowUtc,
            ct);

        var hardDeleteCutoff = nowUtc.Subtract(HardDeleteAfter);
        var hardDeleted = await db.CallHistory
            .Where(x => x.IsDeleted && x.DeletedAtUtc < hardDeleteCutoff)
            .ExecuteDeleteAsync(ct);

        logger.LogInformation(
            "CallHistoryCleanupWorker cleanup completed; soft-deleted {SoftDeletedCount}, hard-deleted {HardDeletedCount}",
            softDeleted,
            hardDeleted);
    }

    private static Task<int> SoftDeleteForModeAsync(
        AppDbContext db,
        AutoDeleteCallHistoryMode mode,
        DateTimeOffset cutoffUtc,
        DateTimeOffset deletedAtUtc,
        CancellationToken ct)
    {
        // Bulk update: one SQL statement per mode, joined to users through the
        // navigation property. This avoids loading users or call rows into memory.
        return db.CallHistory
            .Where(x =>
                !x.IsDeleted &&
                x.CreatedAtUtc < cutoffUtc &&
                x.User != null &&
                x.User.AutoDeleteCallHistoryMode == mode)
            .ExecuteUpdateAsync(
                updates => updates
                    .SetProperty(x => x.IsDeleted, true)
                    .SetProperty(x => x.DeletedAtUtc, deletedAtUtc),
                ct);
    }
}
