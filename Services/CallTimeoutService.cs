using Microsoft.EntityFrameworkCore;
using Workspace.Data;
using Workspace.Domain;

namespace Workspace.Services;

/// <summary>
/// Periodically marks unanswered call sessions as <see cref="CallSessionStatus.Missed"/>.
///
/// A call is "missed" when:
///   - status is still Active
///   - only the creator has joined (no callee participant)
///   - time since StartedAt is greater than the ring timeout (~35s)
///
/// Without this, every unanswered call would stay Active in the DB forever and
/// the user would never see a "missed call" record.
/// </summary>
public sealed class CallTimeoutService(
    IServiceScopeFactory scopeFactory,
    ILogger<CallTimeoutService> logger) : BackgroundService
{
    // How long after start to consider an unanswered call "missed".
    // Slightly longer than the FCM TTL on the incoming-call push (45s) so the
    // caller's ringing UI doesn't time out before the callee's notification.
    private static readonly TimeSpan RingTimeout = TimeSpan.FromSeconds(35);

    // How often to sweep. Coarse — this isn't a critical-path service.
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("CallTimeoutService started — sweep every {Interval}, ring timeout {Timeout}",
            SweepInterval, RingTimeout);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "CallTimeoutService: sweep failed");
            }

            try { await Task.Delay(SweepInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        logger.LogInformation("CallTimeoutService stopped");
    }

    private async Task SweepOnceAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var cutoff = DateTime.UtcNow - RingTimeout;

        // Pull stale Active sessions whose StartedAt is before the cutoff.
        // Then filter in-memory to those with only the creator as a participant —
        // SQL-side equality on `Participants.Count == 1 && Participants.All(p == creator)`
        // is awkward across providers, and the sweep is small.
        var candidates = await db.CallSessions
            .Where(x => x.Status == CallSessionStatus.Active
                        && x.StartedAt != null
                        && x.StartedAt < cutoff)
            .Include(x => x.Participants)
            .ToListAsync(ct);

        if (candidates.Count == 0) return;

        var nowUtc = DateTime.UtcNow;
        var changed = 0;

        foreach (var call in candidates)
        {
            var nonCreatorJoined = call.Participants.Any(p => p.UserId != call.CreatedByUserId);
            if (nonCreatorJoined) continue;

            call.Status = CallSessionStatus.Missed;
            call.EndedAt = nowUtc;
            changed++;
        }

        if (changed > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("CallTimeoutService: marked {Count} calls as Missed", changed);
        }
    }
}
