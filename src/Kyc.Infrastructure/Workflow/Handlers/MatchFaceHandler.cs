using System.Diagnostics;
using Kyc.Domain;
using Kyc.Domain.Decisioning;
using Kyc.Infrastructure.Common;
using Kyc.Infrastructure.Observability;
using Kyc.Infrastructure.Persistence;
using Kyc.Infrastructure.Providers;
using Kyc.Infrastructure.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kyc.Infrastructure.Workflow.Handlers;

// Compares the selfie against the ID document after liveness passes.
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
        var payload = job.ReadPayload<UploadedFileJobPayload>();
        var session = await LoadSessionAsync(db, job.SessionId, ct);
        if (session is null || session.Status != SessionStatus.SelfieSubmitted
            || session.Checks.Any(c => c.UploadedFileId == payload.UploadedFileId && c.Type == CheckType.FaceMatch))
        {
            Logger.LogInformation("Face match no longer applicable; skipping");
            return;
        }

        var selfie = session.UploadedFiles.Single(a => a.Id == payload.UploadedFileId);
        var document = session.UploadedFiles
            .Where(a => a.Type == UploadedFileType.IdentityDocument && a.Status == UploadedFileStatus.Uploaded)
            .OrderByDescending(a => a.UploadedAt)
            .First();

        // Re-verify both images against their declared hashes before the face-match call. A mismatch means the
        // bytes changed since an earlier check, so send it to a human instead of matching.
        if (!await VerifyIntegrityAsync(session, selfie, ct) || !await VerifyIntegrityAsync(session, document, ct))
        {
            RecordCheck(db, session, selfie.Id, CheckType.FaceMatch, "platform", null, CheckOutcome.Unreadable, null,
                [ReasonCodes.UploadIntegrityMismatch], job.Attempts, 0);
            GoToDecision(db, session, "facematch_integrity_mismatch");
            return;
        }

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
