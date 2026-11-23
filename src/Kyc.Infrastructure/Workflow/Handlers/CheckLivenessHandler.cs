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

// Liveness runs first, before the face match.
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
        var payload = job.ReadPayload<UploadedFileJobPayload>();
        var session = await LoadSessionAsync(db, job.SessionId, ct);
        if (session is null || session.Status != SessionStatus.SelfieSubmitted
            || session.Checks.Any(c => c.UploadedFileId == payload.UploadedFileId && c.Type == CheckType.Liveness))
        {
            Logger.LogInformation("Liveness check no longer applicable; skipping");
            return;
        }

        var selfie = session.UploadedFiles.Single(a => a.Id == payload.UploadedFileId);
        var now = Clock.GetUtcNow();

        CheckOutcome outcome;
        if (!await VerifyIntegrityAsync(session, selfie, ct))
        {
            outcome = CheckOutcome.Unreadable;
            RecordCheck(db, session, selfie.Id, CheckType.Liveness, "platform", null, outcome, null,
                [ReasonCodes.UploadIntegrityMismatch], job.Attempts, 0);
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
                db.Enqueue(session.Id, JobType.MatchFace, new UploadedFileJobPayload(selfie.Id), _ctx.CorrelationId, now);
                break;
            case NextStep.RequestRecapture:
                session.Record(AuditEventTypes.RecaptureRequested, new
                {
                    type = nameof(UploadedFileType.Selfie),
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
