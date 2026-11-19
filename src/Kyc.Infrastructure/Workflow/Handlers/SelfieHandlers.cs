using System.Diagnostics;
using Kyc.Domain;
using Kyc.Domain.Decisioning;
using Kyc.Domain.Workflow;
using Kyc.Infrastructure.Common;
using Kyc.Infrastructure.Observability;
using Kyc.Infrastructure.Persistence;
using Kyc.Infrastructure.Providers;
using Kyc.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kyc.Infrastructure.Workflow.Handlers;

// Liveness runs first so we don't pay for a face match on a spoof.
public sealed class CheckLivenessHandler(
    ILivenessProvider provider,
    IObjectStorage storage,
    IOptions<StorageOptions> storageOptions,
    TimeProvider clock,
    KycMetrics metrics,
    IRequestContext requestContext,
    ILogger<CheckLivenessHandler> logger)
    : HandlerBase(storage, storageOptions, clock, metrics, requestContext, logger), IJobHandler
{
    private readonly IRequestContext _ctx = requestContext;
    public JobType Type => JobType.CheckLiveness;

    public async Task HandleAsync(WorkflowJob job, KycDbContext db, CancellationToken ct)
    {
        var payload = job.ReadPayload<ArtifactJobPayload>();
        var session = await LoadSessionAsync(db, job.SessionId, ct);
        if (session is null || session.Status != SessionStatus.SelfieSubmitted
            || session.Checks.Any(c => c.ArtifactId == payload.ArtifactId && c.Type == CheckType.Liveness))
        {
            Logger.LogInformation("Liveness check no longer applicable; skipping");
            return;
        }

        var selfie = session.Artifacts.Single(a => a.Id == payload.ArtifactId);
        var now = Clock.GetUtcNow();

        CheckOutcome outcome;
        if (!await VerifyIntegrityAsync(session, selfie, ct))
        {
            outcome = CheckOutcome.Unreadable;
            RecordCheck(db, session, selfie.Id, CheckType.Liveness, "platform", null, outcome, null,
                [ReasonCodes.ArtifactIntegrityMismatch], job.Attempts, 0);
        }
        else
        {
            var started = Stopwatch.GetTimestamp();
            var result = await provider.CheckAsync(new ProviderRequest(session.Id, selfie.Id,
                await DownloadUrlAsync(selfie), selfie.StorageKey, job.Id.ToString("N"), job.Attempts), ct);
            outcome = result.Outcome;
            RecordCheck(db, session, selfie.Id, CheckType.Liveness, provider.Name, result.ProviderReference,
                result.Outcome, result.Score, result.ReasonCodes, job.Attempts, Elapsed(started));
        }

        switch (WorkflowRules.AfterLivenessCheck(outcome, session.SelfieAttempts))
        {
            case NextStep.Continue:
                // Status stays SelfieSubmitted; face match runs next.
                db.Enqueue(session.Id, JobType.MatchFace, new ArtifactJobPayload(selfie.Id), _ctx.CorrelationId, now);
                break;
            case NextStep.RequestRecapture:
                session.Record(AuditEventTypes.RecaptureRequested, new
                {
                    type = nameof(ArtifactType.Selfie),
                    attemptsUsed = session.SelfieAttempts,
                    attemptsAllowed = VerificationSession.MaxSelfieAttempts,
                });
                session.TransitionTo(SessionStatus.AwaitingSelfie, now, "selfie_recapture_required");
                break;
            case NextStep.GoToDecision:
                GoToDecision(db, session, $"liveness_{outcome}".ToLowerInvariant());
                break;
        }
    }

    public Task OnExhaustedAsync(WorkflowJob job, KycDbContext db, Exception error, CancellationToken ct) =>
        RouteToManualReviewAsync(db, job, error, ct);
}

public sealed class MatchFaceHandler(
    IFaceMatchProvider provider,
    IObjectStorage storage,
    IOptions<StorageOptions> storageOptions,
    TimeProvider clock,
    KycMetrics metrics,
    IRequestContext requestContext,
    ILogger<MatchFaceHandler> logger)
    : HandlerBase(storage, storageOptions, clock, metrics, requestContext, logger), IJobHandler
{
    public JobType Type => JobType.MatchFace;

    public async Task HandleAsync(WorkflowJob job, KycDbContext db, CancellationToken ct)
    {
        var payload = job.ReadPayload<ArtifactJobPayload>();
        var session = await LoadSessionAsync(db, job.SessionId, ct);
        if (session is null || session.Status != SessionStatus.SelfieSubmitted
            || session.Checks.Any(c => c.ArtifactId == payload.ArtifactId && c.Type == CheckType.FaceMatch))
        {
            Logger.LogInformation("Face match no longer applicable; skipping");
            return;
        }

        var selfie = session.Artifacts.Single(a => a.Id == payload.ArtifactId);
        var document = session.Artifacts
            .Where(a => a.Type == ArtifactType.IdentityDocument && a.Status == ArtifactStatus.Uploaded)
            .OrderByDescending(a => a.UploadedAt)
            .First();

        var started = Stopwatch.GetTimestamp();
        var result = await provider.MatchAsync(new FaceMatchRequest(session.Id, selfie.Id,
            await DownloadUrlAsync(selfie), selfie.StorageKey,
            await DownloadUrlAsync(document), document.StorageKey,
            job.Id.ToString("N"), job.Attempts), ct);

        RecordCheck(db, session, selfie.Id, CheckType.FaceMatch, provider.Name, result.ProviderReference,
            result.Outcome, result.Score, result.ReasonCodes, job.Attempts, Elapsed(started));

        GoToDecision(db, session, "checks_completed");
    }

    public Task OnExhaustedAsync(WorkflowJob job, KycDbContext db, Exception error, CancellationToken ct) =>
        RouteToManualReviewAsync(db, job, error, ct);
}
