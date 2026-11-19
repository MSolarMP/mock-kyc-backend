using System.Security.Claims;
using System.Text.RegularExpressions;
using Kyc.Api.Auth;
using Kyc.Api.Contracts;
using Kyc.Api.Infrastructure;
using Kyc.Domain;
using Kyc.Infrastructure.Common;
using Kyc.Infrastructure.Observability;
using Kyc.Infrastructure.Persistence;
using Kyc.Infrastructure.Security;
using Kyc.Infrastructure.Storage;
using Kyc.Infrastructure.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QRCoder;

namespace Kyc.Api.Endpoints;

/// <summary>
/// Customer-device API (web, mobile web, native app). Authenticated with the session-bound bearer token, so there
/// is no session id in any route.
/// </summary>
public static partial class ClientEndpoints
{
    private static readonly HashSet<string> AllowedContentTypes = ["image/jpeg", "image/png"];

    [GeneratedRegex("^[a-fA-F0-9]{64}$")]
    private static partial Regex Sha256Hex();

    public static void MapClientEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/client/session").WithTags("Customer device")
            .RequireAuthorization(Policies.Customer)
            .RequireRateLimiting("customer");

        group.MapGet("", GetSession).WithSummary("Current status and what the UI should do next (poll this)");
        group.MapPost("/handoff", CreateHandoff)
            .WithSummary("Create a single-use QR handoff link to continue on another device");
        group.MapPost("/artifacts", CreateArtifact)
            .WithSummary("Declare an upload and receive a short-lived pre-signed PUT URL");
        group.MapPost("/artifacts/{artifactId:guid}/complete", CompleteArtifact)
            .WithSummary("Confirm the upload finished; starts the verification steps for it");

        app.MapPost("/v1/handoff/redeem", RedeemHandoff)
            .WithTags("Customer device")
            .AllowAnonymous()
            .RequireRateLimiting("handoff-redeem")
            .WithSummary("Exchange a QR handoff token for a device token on the second device");
    }

    private static async Task<IResult> GetSession(ClaimsPrincipal user, KycDbContext db, TimeProvider clock,
        CancellationToken ct)
    {
        var session = await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == user.SessionId(), ct);
        return Results.Ok(session.ToClientView(clock.GetUtcNow()));
    }

    private static async Task<IResult> CreateHandoff(ClaimsPrincipal user, KycDbContext db, TimeProvider clock,
        KycMetrics metrics, IOptions<KycApiOptions> options, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var session = await db.Sessions.SingleAsync(s => s.Id == user.SessionId(), ct);
        session.EnsureUsableByCustomer(now);
        if (session.IsTerminal || session.Status == SessionStatus.ManualReview)
            throw new SessionStateConflictException("session_complete", "There is nothing left for the customer to do.");

        // At most one live QR per session: issuing a new one revokes earlier unredeemed ones (e.g. a page refresh).
        var live = await db.HandoffTokens
            .Where(h => h.SessionId == session.Id && h.RedeemedAt == null && h.RevokedAt == null && h.ExpiresAt > now)
            .ToListAsync(ct);
        foreach (var h in live) h.Revoke(now);

        var token = Tokens.Generate();
        var handoff = HandoffToken.Create(session.Id, Tokens.Hash(token), user.AccessTokenId(),
            TimeSpan.FromSeconds(options.Value.HandoffTtlSeconds), now);
        db.HandoffTokens.Add(handoff);
        session.Record(AuditEventTypes.HandoffIssued, new { handoffId = handoff.Id, expiresAt = handoff.ExpiresAt, revokedPrevious = live.Count });
        await db.SaveChangesAsync(ct);
        metrics.Handoff("issued");

        // Token in the URL *fragment*: browsers don't send fragments to servers, so it stays out of access logs,
        // proxies and Referer headers.
        var url = $"{options.Value.HandoffBaseUrl}#token={token}";
        using var qr = new QRCodeGenerator().CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(qr).GetGraphic(8);
        return Results.Ok(new CreateHandoffResponse(url, handoff.ExpiresAt, Convert.ToBase64String(png)));
    }

    private static async Task<IResult> RedeemHandoff(RedeemHandoffRequest request, KycDbContext db,
        RequestContext requestContext, TimeProvider clock, KycMetrics metrics, ILoggerFactory loggers,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Token) || request.Token.Length > 100)
            return Problems.Validation((nameof(request.Token), "Required."));

        // Unauthenticated until the token checks out: attribute any audit events to an anonymous device.
        requestContext.SetActor(ActorType.Customer, "anonymous");
        var hash = Tokens.Hash(request.Token);
        var now = clock.GetUtcNow();
        var ip = requestContext.IpAddress;
        var ua = requestContext.UserAgent;

        // Atomic single-use: only one concurrent redeem can flip redeemed_at from NULL.
        var claimed = await db.HandoffTokens
            .Where(h => h.TokenHash == hash && h.RedeemedAt == null && h.RevokedAt == null && h.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(h => h.RedeemedAt, now)
                .SetProperty(h => h.RedeemedIp, ip)
                .SetProperty(h => h.RedeemedUserAgent, ua), ct);

        var handoff = await db.HandoffTokens.AsNoTracking().SingleOrDefaultAsync(h => h.TokenHash == hash, ct);
        if (claimed == 0)
        {
            metrics.Handoff("rejected");
            if (handoff is not null)
            {
                // A known token replayed or used late: worth recording as a possible signal of a stolen QR.
                var target = await db.Sessions.SingleAsync(s => s.Id == handoff.SessionId, ct);
                target.Record(AuditEventTypes.HandoffRedeemFailed, new
                {
                    handoffId = handoff.Id,
                    reason = handoff.RedeemedAt is not null ? "already_redeemed"
                        : handoff.RevokedAt is not null ? "revoked" : "expired",
                });
                await db.SaveChangesAsync(ct);
            }
            loggers.CreateLogger("Handoff").LogWarning("Handoff redeem rejected");
            // Same response for unknown, used and expired tokens: don't help anyone probe for valid ones.
            throw new BusinessRuleException("handoff_invalid", "This link is invalid or has expired. Generate a new QR code.");
        }

        var session = await db.Sessions.SingleAsync(s => s.Id == handoff!.SessionId, ct);
        session.EnsureUsableByCustomer(now);

        var channel = ua?.Contains("Mobile", StringComparison.OrdinalIgnoreCase) == true ? Channel.MobileWeb : Channel.Web;
        var (token, accessToken) = AccessTokens.Issue(db, session, channel, viaHandoff: true, ip, ua,
            request.DeviceFingerprint, now);
        requestContext.SetActor(ActorType.Customer, $"token:{accessToken.Id}");
        session.Record(AuditEventTypes.HandoffRedeemed, new
        {
            handoffId = handoff!.Id,
            accessTokenId = accessToken.Id,
            issuedByTokenId = handoff.IssuedByTokenId,
            channel = channel.ToString(),
            deviceFingerprintProvided = request.DeviceFingerprint is not null,
        });
        await db.SaveChangesAsync(ct);
        metrics.Handoff("redeemed");

        return Results.Ok(new RedeemHandoffResponse(token, accessToken.ExpiresAt, session.ToClientView(now)));
    }

    private static async Task<IResult> CreateArtifact(CreateArtifactRequest request, ClaimsPrincipal user,
        KycDbContext db, IObjectStorage storage, IOptions<StorageOptions> storageOptions, TimeProvider clock,
        CancellationToken ct)
    {
        var o = storageOptions.Value;
        var errors = new List<(string, string)>();
        if (!AllowedContentTypes.Contains(request.ContentType))
            errors.Add((nameof(request.ContentType), "Must be image/jpeg or image/png."));
        if (request.SizeBytes <= 0 || request.SizeBytes > o.MaxUploadBytes)
            errors.Add((nameof(request.SizeBytes), $"Must be between 1 and {o.MaxUploadBytes} bytes."));
        if (request.Sha256 is null || !Sha256Hex().IsMatch(request.Sha256))
            errors.Add((nameof(request.Sha256), "Must be a 64-character hex SHA-256 of the file."));
        if (errors.Count > 0) return Problems.Validation([.. errors]);

        var now = clock.GetUtcNow();
        var session = await db.Sessions.Include(s => s.Artifacts).SingleAsync(s => s.Id == user.SessionId(), ct);
        session.EnsureUsableByCustomer(now);

        var artifact = session.AddArtifact(request.Type, request.ContentType, request.SizeBytes, request.Sha256!,
            user.AccessTokenId(), now);
        await db.SaveChangesAsync(ct);

        var ttl = TimeSpan.FromSeconds(o.UploadUrlTtlSeconds);
        var url = await storage.CreateUploadUrlAsync(artifact.StorageKey, artifact.ContentType, ttl);
        return Results.Created($"/v1/client/session/artifacts/{artifact.Id}", new CreateArtifactResponse(
            artifact.Id, url.ToString(), "PUT",
            new Dictionary<string, string> { ["Content-Type"] = artifact.ContentType },
            now.Add(ttl)));
    }

    private static async Task<IResult> CompleteArtifact(Guid artifactId, ClaimsPrincipal user, KycDbContext db,
        IObjectStorage storage, RequestContext requestContext, TimeProvider clock, KycMetrics metrics,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var session = await db.Sessions.Include(s => s.Artifacts).SingleAsync(s => s.Id == user.SessionId(), ct);
        var artifact = session.Artifacts.SingleOrDefault(a => a.Id == artifactId)
                       ?? throw new NotFoundException("Artifact");

        // Idempotent: a retried "complete" (lost response, double tap) gets the same answer.
        if (artifact.Status == ArtifactStatus.Uploaded)
            return Results.Accepted(value: session.ToClientView(now));
        if (artifact.Status != ArtifactStatus.PendingUpload)
            throw new SessionStateConflictException("artifact_not_pending", $"Artifact is {artifact.Status}.");

        session.EnsureUsableByCustomer(now);

        // Trust but verify: the object must exist, and its size and type must match what was declared.
        // The worker re-hashes the bytes before any vendor sees them.
        var meta = await storage.GetMetadataAsync(artifact.StorageKey, ct)
                   ?? throw new BusinessRuleException("upload_not_found", "No uploaded file was found. Upload it to the URL first.");
        if (meta.SizeBytes != artifact.DeclaredSizeBytes)
            throw new BusinessRuleException("upload_size_mismatch", "The uploaded file size does not match the declared size.");
        if (meta.ContentType is not null && meta.ContentType != artifact.ContentType)
            throw new BusinessRuleException("upload_type_mismatch", "The uploaded content type does not match the declared type.");

        artifact.MarkUploaded(now);
        session.Record(AuditEventTypes.ArtifactUploaded, new
        {
            artifactId = artifact.Id,
            type = artifact.Type.ToString(),
            sizeBytes = meta.SizeBytes,
            sha256 = artifact.DeclaredSha256,
        });
        session.SubmitArtifact(artifact, now);

        var jobType = artifact.Type == ArtifactType.IdentityDocument ? JobType.ValidateDocument : JobType.CheckLiveness;
        db.Enqueue(session.Id, jobType, new ArtifactJobPayload(artifact.Id), requestContext.CorrelationId, now);

        // State change + audit + job, all in one transaction.
        await db.SaveChangesAsync(ct);
        metrics.ArtifactUploaded(artifact.Type.ToString());
        return Results.Accepted(value: session.ToClientView(now));
    }
}
