using System.Text.Json;
using Kyc.Domain;
using Kyc.Domain.Decisioning;
using Kyc.Infrastructure.Common;
using Kyc.Infrastructure.Observability;
using Kyc.Infrastructure.Persistence;
using Kyc.Infrastructure.Providers;
using Kyc.Infrastructure.Security;
using Kyc.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kyc.Infrastructure.Workflow.Handlers;

// Gathers check results and cross-session signals, then runs the decision engine.
public sealed class AssessRiskAndDecideHandler(
    DecisionEngine engine,
    FieldProtector protector,
    IObjectStorage storage,
    IOptions<StorageOptions> storageOptions,
    TimeProvider clock,
    KycMetrics metrics,
    IRequestContext requestContext,
    ILogger<AssessRiskAndDecideHandler> logger)
    : HandlerBase(storage, storageOptions, clock, metrics, requestContext, logger), IJobHandler
{
    public JobType Type => JobType.AssessRiskAndDecide;

    public async Task HandleAsync(WorkflowJob job, KycDbContext db, CancellationToken ct)
    {
        var session = await LoadSessionAsync(db, job.SessionId, ct);
        if (session is null || session.Status != SessionStatus.PendingDecision)
        {
            Logger.LogInformation("Decision no longer applicable; skipping");
            return;
        }

        var now = Clock.GetUtcNow();
        var signals = await GatherSignalsAsync(db, session, now, ct);
        session.Record(AuditEventTypes.RiskAssessed, signals);

        var input = new DecisionInput(
            Latest(session, CheckType.DocumentValidation),
            Latest(session, CheckType.Liveness),
            Latest(session, CheckType.FaceMatch),
            signals);
        var result = engine.Decide(input);

        session.ApplyDecision(Decision.Automated(session.Id, result.Outcome, result.ReasonCodes, result.RulesVersion,
            result.RiskScore, now), now);
        Metrics.Decision(result.Outcome.ToString(), nameof(ActorType.System));
        Logger.LogInformation("Session decided {Outcome} ({Reasons})", result.Outcome, string.Join(",", result.ReasonCodes));
    }

    private async Task<RiskSignals> GatherSignalsAsync(KycDbContext db, VerificationSession s, DateTimeOffset now,
        CancellationToken ct)
    {
        int priorRejections = 0, openSameDoc = 0;
        if (s.DocumentNumberHash is { } docHash)
        {
            var others = db.Sessions.Where(o => o.DocumentNumberHash == docHash && o.Id != s.Id);
            priorRejections = await others.CountAsync(o => o.Status == SessionStatus.Rejected, ct);
            openSameDoc = await others.CountAsync(o =>
                o.Status != SessionStatus.Rejected && o.Status != SessionStatus.Approved &&
                o.Status != SessionStatus.Expired, ct);
        }

        var ipCount = s.CreatedIp is null
            ? 0
            : await db.Sessions.CountAsync(o => o.CreatedIp == s.CreatedIp && o.CreatedAt > now.AddHours(-24), ct);

        var selfieHash = s.UploadedFiles
            .Where(a => a.Type == UploadedFileType.Selfie && a.Status == UploadedFileStatus.Uploaded)
            .OrderByDescending(a => a.UploadedAt)
            .Select(a => a.DeclaredSha256)
            .FirstOrDefault();
        var selfieReuse = selfieHash is null
            ? 0
            : await db.UploadedFiles.CountAsync(a => a.DeclaredSha256 == selfieHash && a.SessionId != s.Id
                                                 && a.Type == UploadedFileType.Selfie, ct);

        var expired = false;
        if (s.ExtractedIdentityEncrypted is { } encrypted)
        {
            var identity = JsonSerializer.Deserialize<ExtractedIdentity>(protector.Unprotect(encrypted), KycDbContext.Json);
            expired = identity?.ExpiryDate is { } expiry && expiry < DateOnly.FromDateTime(now.UtcDateTime);
        }

        return new RiskSignals(priorRejections, openSameDoc, ipCount, selfieReuse, expired);
    }

    private static CheckSummary? Latest(VerificationSession s, CheckType type) =>
        s.Checks.Where(c => c.Type == type)
            .OrderByDescending(c => c.CompletedAt)
            .Select(c => new CheckSummary(c.Outcome, c.Score, c.ReasonCodes))
            .FirstOrDefault();

    public Task OnExhaustedAsync(WorkflowJob job, KycDbContext db, Exception error, CancellationToken ct) =>
        RouteToManualReviewAsync(db, job, error, ct);
}
