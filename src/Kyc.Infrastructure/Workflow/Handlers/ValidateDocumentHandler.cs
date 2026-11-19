using System.Diagnostics;
using System.Text.Json;
using Kyc.Domain;
using Kyc.Domain.Decisioning;
using Kyc.Domain.Workflow;
using Kyc.Infrastructure.Common;
using Kyc.Infrastructure.Observability;
using Kyc.Infrastructure.Persistence;
using Kyc.Infrastructure.Providers;
using Kyc.Infrastructure.Security;
using Kyc.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kyc.Infrastructure.Workflow.Handlers;

public sealed class ValidateDocumentHandler(
    IDocumentVerificationProvider provider,
    FieldProtector protector,
    IdentifierHasher hasher,
    IObjectStorage storage,
    IOptions<StorageOptions> storageOptions,
    TimeProvider clock,
    KycMetrics metrics,
    IRequestContext requestContext,
    ILogger<ValidateDocumentHandler> logger)
    : HandlerBase(storage, storageOptions, clock, metrics, requestContext, logger), IJobHandler
{
    public JobType Type => JobType.ValidateDocument;

    public async Task HandleAsync(WorkflowJob job, KycDbContext db, CancellationToken ct)
    {
        var payload = job.ReadPayload<ArtifactJobPayload>();
        var session = await LoadSessionAsync(db, job.SessionId, ct);

        // Redelivered job: the session already moved on.
        if (session is null || session.Status != SessionStatus.DocumentSubmitted
            || session.Checks.Any(c => c.ArtifactId == payload.ArtifactId && c.Type == CheckType.DocumentValidation))
        {
            Logger.LogInformation("Document validation no longer applicable; skipping");
            return;
        }

        var artifact = session.Artifacts.Single(a => a.Id == payload.ArtifactId);
        var now = Clock.GetUtcNow();

        CheckOutcome outcome;
        if (!await VerifyIntegrityAsync(session, artifact, ct))
        {
            outcome = CheckOutcome.Unreadable;
            RecordCheck(db, session, artifact.Id, CheckType.DocumentValidation, "platform", null, outcome, null,
                [ReasonCodes.ArtifactIntegrityMismatch], job.Attempts, 0);
        }
        else
        {
            var started = Stopwatch.GetTimestamp();
            var result = await provider.VerifyAsync(new ProviderRequest(session.Id, artifact.Id,
                await DownloadUrlAsync(artifact), artifact.StorageKey, job.Id.ToString("N"), job.Attempts), ct);
            outcome = result.Outcome;
            RecordCheck(db, session, artifact.Id, CheckType.DocumentValidation, provider.Name, result.ProviderReference,
                result.Outcome, result.Score, result.ReasonCodes, job.Attempts, Elapsed(started));

            if (result.Identity is { } identity)
            {
                // Document number stored as a keyed hash, the rest encrypted.
                session.RecordExtractedIdentity(
                    hasher.Hash(identity.DocumentNumber),
                    protector.Protect(JsonSerializer.Serialize(identity, KycDbContext.Json)));
            }
        }

        switch (WorkflowRules.AfterDocumentCheck(outcome, session.DocumentAttempts))
        {
            case NextStep.Continue:
                session.TransitionTo(SessionStatus.AwaitingSelfie, now, $"document_{outcome}".ToLowerInvariant());
                break;
            case NextStep.RequestRecapture:
                session.Record(AuditEventTypes.RecaptureRequested, new
                {
                    type = nameof(ArtifactType.IdentityDocument),
                    attemptsUsed = session.DocumentAttempts,
                    attemptsAllowed = VerificationSession.MaxDocumentAttempts,
                });
                session.TransitionTo(SessionStatus.Created, now, "document_recapture_required");
                break;
            case NextStep.GoToDecision:
                GoToDecision(db, session, $"document_{outcome}".ToLowerInvariant());
                break;
        }
    }

    public Task OnExhaustedAsync(WorkflowJob job, KycDbContext db, Exception error, CancellationToken ct) =>
        RouteToManualReviewAsync(db, job, error, ct);
}
