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

namespace Kyc.Api.Endpoints;

// Customer-device API the applicant's phone/browser calls; the session-bound token identifies the session.
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
        group.MapPost("/uploads", CreateUpload)
            .WithSummary("Declare an upload and receive a short-lived pre-signed PUT URL");
        group.MapPost("/uploads/{uploadedFileId:guid}/complete", CompleteUpload)
            .WithSummary("Confirm the upload finished; starts the verification steps for it");

        app.MapPost("/v1/handoff/redeem", RedeemHandoff)
            .WithTags("Customer device")
            .AllowAnonymous()
            .RequireRateLimiting("handoff-redeem")
            .WithSummary("Exchange a QR handoff token for a device token on the second device");
    }

    // Current status + the next action the device UI should take; the device polls this.
    private static async Task<IResult> GetSession(ClaimsPrincipal user, KycDbContext db, TimeProvider clock,
        CancellationToken ct)
    {
        var session = await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == user.SessionId(), ct);
        return Results.Ok(session.ToClientView(clock.GetUtcNow()));
    }

    // Issues a single-use QR link to continue on another device; revokes any earlier unredeemed QR.
    private static async Task<IResult> CreateHandoff(ClaimsPrincipal user, KycDbContext db, TimeProvider clock,
        KycMetrics metrics, IOptions<KycApiOptions> options, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var session = await db.Sessions.SingleAsync(s => s.Id == user.SessionId(), ct);
        session.EnsureUsableByCustomer(now);
        if (session.IsTerminal || session.Status == SessionStatus.ManualReview)
            throw new SessionStateConflictException("session_complete", "There is nothing left for the customer to do.");

        // One live QR per session. A new one revokes any unredeemed ones (e.g. page refresh).
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

        // Token goes in the URL fragment.
        var url = $"{options.Value.HandoffBaseUrl}#token={token}";
        return Results.Ok(new CreateHandoffResponse(url, handoff.ExpiresAt));
    }

    // Second device swaps the QR token for its own session token; single-use, one generic error for bad/used/expired.
    private static async Task<IResult> RedeemHandoff(RedeemHandoffRequest request, KycDbContext db,
        RequestContext requestContext, TimeProvider clock, KycMetrics metrics, ILoggerFactory loggers,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Token) || request.Token.Length > 100)
            return Problems.Validation((nameof(request.Token), "Required."));

        // Not authenticated yet, so audit events go to an anonymous device.
        requestContext.SetActor(ActorType.Customer, "anonymous");
        var hash = Tokens.Hash(request.Token);
        var now = clock.GetUtcNow();
        var ip = requestContext.IpAddress;
        var ua = requestContext.UserAgent;

        // Single use: only one concurrent redeem can flip redeemed_at from NULL.
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
                // Known token replayed or used late.
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
            // Same error for unknown, used and expired tokens.
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

    // Upload step 1: declare the file (type, size, SHA-256), get a short-lived pre-signed PUT URL; bytes go straight to storage.
    private static async Task<IResult> CreateUpload(CreateUploadRequest request, ClaimsPrincipal user,
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
        var session = await db.Sessions.Include(s => s.UploadedFiles).SingleAsync(s => s.Id == user.SessionId(), ct);
        session.EnsureUsableByCustomer(now);

        var uploadedFile = session.AddUploadedFile(request.Type, request.ContentType, request.SizeBytes, request.Sha256!,
            user.AccessTokenId(), now);
        await db.SaveChangesAsync(ct);

        var ttl = TimeSpan.FromSeconds(o.UploadUrlTtlSeconds);
        var url = await storage.CreateUploadUrlAsync(uploadedFile.StorageKey, uploadedFile.ContentType, ttl);
        return Results.Created($"/v1/client/session/uploads/{uploadedFile.Id}", new CreateUploadResponse(
            uploadedFile.Id, url.ToString(), "PUT",
            new Dictionary<string, string> { ["Content-Type"] = uploadedFile.ContentType },
            now.Add(ttl)));
    }

    // Upload step 2: confirm the bytes landed and match, seal them, advance the session, queue the first check.
    private static async Task<IResult> CompleteUpload(Guid uploadedFileId, ClaimsPrincipal user, KycDbContext db,
        IObjectStorage storage, RequestContext requestContext, TimeProvider clock, KycMetrics metrics,
        ILoggerFactory loggers, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var session = await db.Sessions.Include(s => s.UploadedFiles).SingleAsync(s => s.Id == user.SessionId(), ct);
        var uploadedFile = session.UploadedFiles.SingleOrDefault(a => a.Id == uploadedFileId)
                       ?? throw new NotFoundException("UploadedFile");

        // A retried complete (lost response, double tap) gets the same answer.
        if (uploadedFile.Status == UploadedFileStatus.Uploaded)
            return Results.Accepted(value: session.ToClientView(now));
        if (uploadedFile.Status != UploadedFileStatus.PendingUpload)
            throw new SessionStateConflictException("upload_not_pending", $"This upload is {uploadedFile.Status}.");

        session.EnsureUsableByCustomer(now);

        // Check the object exists and matches the declared size and type.
        // The worker re-hashes the bytes before any vendor sees them.
        var meta = await storage.GetMetadataAsync(uploadedFile.StorageKey, ct)
                   ?? throw new BusinessRuleException("upload_not_found", "No uploaded file was found. Upload it to the URL first.");
        if (meta.SizeBytes != uploadedFile.DeclaredSizeBytes)
            throw new BusinessRuleException("upload_size_mismatch", "The uploaded file size does not match the declared size.");
        if (meta.ContentType is not null && meta.ContentType != uploadedFile.ContentType)
            throw new BusinessRuleException("upload_type_mismatch", "The uploaded content type does not match the declared type.");

        // Copy the bytes to a sealed key the client has no upload URL for, then point the uploaded file at it.
        // A later overwrite of the original upload key then can't change what vendors or reviewers read.
        var uploadKey = uploadedFile.StorageKey;
        var sealedKey = UploadedFile.BuildStorageKey(session.Id, uploadedFile.Type,
            $"{uploadedFile.Id:N}.{Guid.NewGuid():N}.{UploadedFile.Extension(uploadedFile.ContentType)}");
        await storage.CopyAsync(uploadKey, sealedKey, ct);
        uploadedFile.MarkUploaded(sealedKey, now);
        session.Record(AuditEventTypes.UploadCompleted, new
        {
            uploadedFileId = uploadedFile.Id,
            type = uploadedFile.Type.ToString(),
            sizeBytes = meta.SizeBytes,
            sha256 = uploadedFile.DeclaredSha256,
        });
        session.SubmitUploadedFile(uploadedFile, now);

        var jobType = uploadedFile.Type == UploadedFileType.IdentityDocument ? JobType.ValidateDocument : JobType.CheckLiveness;
        db.Enqueue(session.Id, jobType, new UploadedFileJobPayload(uploadedFile.Id), requestContext.CorrelationId, now);

        // State change, audit and job in one transaction.
        await db.SaveChangesAsync(ct);

        // Drop the now-unreferenced upload key. Best effort: an orphan is harmless, so a failure here
        // must not fail a completed upload.
        try { await storage.DeleteAsync(uploadKey, ct); }
        catch (Exception ex) { loggers.CreateLogger("UploadedFiles").LogWarning(ex, "Failed to delete superseded upload key"); }

        metrics.UploadCompleted(uploadedFile.Type.ToString());
        return Results.Accepted(value: session.ToClientView(now));
    }
}
