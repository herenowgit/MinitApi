using Microsoft.EntityFrameworkCore;
using Workspace.Data;
using Workspace.Domain;

namespace Workspace.Services;

/// <summary>
/// Periodically hard-deletes messages whose sender's <see cref="AutoDeleteMessageMode"/>
/// retention window has elapsed. Mirrors <see cref="CallHistoryCleanupWorker"/> but, per
/// the message feature spec, removes rows outright rather than soft-deleting first.
/// The worker only reads timestamps and the sender's setting — never message content.
/// </summary>
public sealed class MessageCleanupWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<MessageCleanupWorker> logger) : BackgroundService
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("MessageCleanupWorker started; cleanup interval is {Interval}", CleanupInterval);

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
                logger.LogError(ex, "MessageCleanupWorker cleanup cycle failed");
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

        logger.LogInformation("MessageCleanupWorker stopped");
    }

    private async Task CleanupOnceAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var nowUtc = DateTimeOffset.UtcNow;

        logger.LogInformation("MessageCleanupWorker cleanup started at {NowUtc}", nowUtc);

        var deleted = 0;
        deleted += await HardDeleteForModeAsync(db, AutoDeleteMessageMode.OneHour, nowUtc.AddHours(-1), ct);
        deleted += await HardDeleteForModeAsync(db, AutoDeleteMessageMode.SixHours, nowUtc.AddHours(-6), ct);

        // EndOfDay uses UTC midnight as the simplified boundary: anything created
        // before today's UTC date is old enough to remove.
        deleted += await HardDeleteForModeAsync(
            db,
            AutoDeleteMessageMode.EndOfDay,
            new DateTimeOffset(nowUtc.UtcDateTime.Date, TimeSpan.Zero),
            ct);

        // AutoDeleteMessageMode.Never is intentionally never processed.

        logger.LogInformation("MessageCleanupWorker cleanup completed; hard-deleted {DeletedCount}", deleted);
    }

    private static Task<int> HardDeleteForModeAsync(
        AppDbContext db,
        AutoDeleteMessageMode mode,
        DateTimeOffset cutoffUtc,
        CancellationToken ct)
    {
        // Sender governs: a message is removed once the sending user's retention
        // window has elapsed. One bulk DELETE per mode, joined to the sender
        // through the navigation property — no rows are loaded into memory.
        return db.Messages
            .Where(x =>
                x.CreatedAtUtc < cutoffUtc &&
                x.SenderUser.AutoDeleteMessageMode == mode)
            .ExecuteDeleteAsync(ct);
    }
}
