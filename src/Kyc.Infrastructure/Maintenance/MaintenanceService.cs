using Kyc.Domain;
using Kyc.Infrastructure.Common;
using Kyc.Infrastructure.Persistence;
using Kyc.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kyc.Infrastructure.Maintenance;

// How often the maintenance sweep runs.
public sealed class RetentionOptions
{
    public const string Section = "Retention";
    public int SweepIntervalSeconds { get; set; } = 60;
}

// Expires abandoned sessions and purges images past the partner's retention period.
// Decisions and audit rows are kept.
public sealed class MaintenanceService(
    IServiceScopeFactory scopes,
    IOptions<RetentionOptions> options,
    TimeProvider clock,
    ILogger<MaintenanceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.SweepIntervalSeconds), clock);
        do
        {
            try
            {
                await ExpireAbandonedSessionsAsync(stoppingToken);
                await PurgeImagesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Maintenance sweep failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    // Marks sessions that timed out waiting on the customer as Expired.
    internal async Task<int> ExpireAbandonedSessionsAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KycDbContext>();
        ((RequestContext)scope.ServiceProvider.GetRequiredService<IRequestContext>())
            .SetActor(ActorType.System, "maintenance");

        var now = clock.GetUtcNow();
        var lapsed = await db.Sessions
            .Where(s => (s.Status == SessionStatus.Created || s.Status == SessionStatus.AwaitingSelfie) && s.ExpiresAt <= now)
            .OrderBy(s => s.ExpiresAt)
            .Take(200)
            .ToListAsync(ct);

        foreach (var s in lapsed) s.TransitionTo(SessionStatus.Expired, now, "customer_inactive");
        if (lapsed.Count > 0)
        {
            // Rows changed in the meantime fail the xmin check and get picked up next sweep.
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateConcurrencyException) { logger.LogInformation("Expiry sweep raced with customer activity; will retry"); }
            logger.LogInformation("Expired {Count} abandoned sessions", lapsed.Count);
        }
        return lapsed.Count;
    }

    // Deletes stored images past each partner's retention window, keeping the metadata rows.
    internal async Task<int> PurgeImagesAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KycDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IObjectStorage>();
        ((RequestContext)scope.ServiceProvider.GetRequiredService<IRequestContext>())
            .SetActor(ActorType.System, "retention");

        // Retention is per partner.
        var now = clock.GetUtcNow();
        var partners = await db.Partners.AsNoTracking()
            .Select(p => new { p.Id, p.ImageRetentionDays })
            .ToListAsync(ct);

        var sessions = new List<VerificationSession>();
        foreach (var partner in partners)
        {
            var cutoff = now.AddDays(-partner.ImageRetentionDays);
            sessions.AddRange(await db.Sessions
                .Include(s => s.UploadedFiles)
                .Where(s => s.PartnerId == partner.Id && s.CompletedAt != null && s.CompletedAt < cutoff
                            && s.UploadedFiles.Any(a => a.Status != UploadedFileStatus.Purged))
                .OrderBy(s => s.CompletedAt)
                .Take(50)
                .ToListAsync(ct));
        }

        var purged = 0;
        foreach (var session in sessions)
        {
            foreach (var uploadedFile in session.UploadedFiles.Where(a => a.Status != UploadedFileStatus.Purged))
            {
                await storage.DeleteAsync(uploadedFile.StorageKey, ct); // Deleting a missing key is fine.
                uploadedFile.MarkPurged(clock.GetUtcNow());
                session.Record(AuditEventTypes.UploadPurged, new { uploadedFileId = uploadedFile.Id, reason = "retention_policy" });
                purged++;
            }
        }
        if (purged > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Purged {Count} images past retention", purged);
        }
        return purged;
    }
}
