using System.Diagnostics;
using System.Security.Cryptography;
using Kyc.Domain;
using Kyc.Domain.Decisioning;
using Kyc.Infrastructure.Common;
using Kyc.Infrastructure.Observability;
using Kyc.Infrastructure.Persistence;
using Kyc.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kyc.Infrastructure.Workflow.Handlers;

// Shared helpers for the job handlers: load a session, verify file integrity, record a check, move to a decision or a human.
public abstract class HandlerBase(
    IObjectStorage storage,
    IOptions<StorageOptions> storageOptions,
    TimeProvider clock,
    KycMetrics metrics,
    IRequestContext requestContext,
    ILogger logger)
{
    protected TimeProvider Clock => clock;
    protected KycMetrics Metrics => metrics;
    protected ILogger Logger => logger;
    protected IObjectStorage Storage => storage;

    // Loads a session together with its uploads, checks and decisions.
    protected static Task<VerificationSession?> LoadSessionAsync(KycDbContext db, Guid id, CancellationToken ct) =>
        db.Sessions
            .Include(s => s.UploadedFiles)
            .Include(s => s.Checks)
            .Include(s => s.Decisions)
            .AsSplitQuery()
            .SingleOrDefaultAsync(s => s.Id == id, ct);

    protected Task<Uri> DownloadUrlAsync(UploadedFile a) =>
        storage.CreateDownloadUrlAsync(a.StorageKey, TimeSpan.FromSeconds(storageOptions.Value.DownloadUrlTtlSeconds));

    // Re-hash the stored bytes against the declared hash to catch a swapped or corrupted file. Checked on
    // every call, not cached, so each step verifies the bytes it's about to use.
    protected async Task<bool> VerifyIntegrityAsync(VerificationSession session, UploadedFile uploadedFile, CancellationToken ct)
    {
        await using var stream = await storage.OpenReadAsync(uploadedFile.StorageKey, ct);
        var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
        if (actual == uploadedFile.DeclaredSha256)
        {
            uploadedFile.MarkIntegrityVerified();
            return true;
        }
        session.Record(AuditEventTypes.UploadIntegrityFailed, new { uploadedFileId = uploadedFile.Id });
        logger.LogWarning("UploadedFile {UploadedFileId} failed integrity verification", uploadedFile.Id);
        return false;
    }

    // Saves a provider check, records check.completed, and emits the provider-call metric.
    protected ProviderCheck RecordCheck(KycDbContext db, VerificationSession session, Guid uploadedFileId, CheckType type,
        string provider, string? reference, CheckOutcome outcome, decimal? score, IReadOnlyList<string> reasons,
        int attempt, long durationMs)
    {
        var check = ProviderCheck.Create(session.Id, uploadedFileId, type, provider, reference, outcome, score, reasons,
            attempt, durationMs, clock.GetUtcNow());
        db.ProviderChecks.Add(check);
        session.Checks.Add(check);
        session.Record(AuditEventTypes.CheckCompleted, new
        {
            checkId = check.Id,
            type = type.ToString(),
            provider,
            providerReference = reference,
            outcome = outcome.ToString(),
            score,
            reasons,
            durationMs,
        });
        metrics.ProviderCall(type.ToString(), provider, outcome.ToString(), durationMs);
        return check;
    }

    // Moves the session to PendingDecision and queues the AssessRiskAndDecide job.
    protected void GoToDecision(KycDbContext db, VerificationSession session, string reason)
    {
        session.TransitionTo(SessionStatus.PendingDecision, clock.GetUtcNow(), reason);
        db.Enqueue(session.Id, JobType.AssessRiskAndDecide, new EmptyPayload(), requestContext.CorrelationId, clock.GetUtcNow());
    }

    // Vendor down after all retries. Send it to a human.
    protected async Task RouteToManualReviewAsync(KycDbContext db, WorkflowJob job, Exception error, CancellationToken ct)
    {
        var session = await LoadSessionAsync(db, job.SessionId, ct);
        if (session is null || session.IsTerminal || session.Status == SessionStatus.ManualReview) return;

        var now = clock.GetUtcNow();
        session.Record(AuditEventTypes.CheckProviderUnavailable, new
        {
            jobType = job.Type.ToString(),
            attempts = job.Attempts,
            error = error.GetType().Name,
        });
        session.ApplyDecision(Decision.Automated(session.Id, DecisionOutcome.ManualReview,
            [ReasonCodes.ProviderUnavailable], "n/a", 50, now), now);
        metrics.Decision(nameof(DecisionOutcome.ManualReview), nameof(ActorType.System));
    }

    protected static long Elapsed(long startTimestamp) =>
        (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
}
