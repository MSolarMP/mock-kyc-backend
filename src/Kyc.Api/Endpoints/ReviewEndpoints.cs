using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kyc.Api.Auth;
using Kyc.Api.Contracts;
using Kyc.Api.Infrastructure;
using Kyc.Domain;
using Kyc.Infrastructure.Common;
using Kyc.Infrastructure.Observability;
using Kyc.Infrastructure.Persistence;
using Kyc.Infrastructure.Providers;
using Kyc.Infrastructure.Security;
using Kyc.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kyc.Api.Endpoints;

/// <summary>
/// Manual review API, called by the partner's back-office tool on behalf of one of its staff. The partner already
/// authenticates its reviewers, so it passes the reviewer's identity in <c>X-Reviewer-Id</c> (an internal staff id,
/// not an email, to keep personal data out of the audit trail). Every decision and every view of personal data is
/// audited against that named reviewer. Production option: OIDC tokens carrying the reviewer identity (see docs).
/// </summary>
public static partial class ReviewEndpoints
{
    public const string ReviewerHeader = "X-Reviewer-Id";

    [GeneratedRegex("^[A-Za-z0-9._@:-]{1,64}$")]
    private static partial Regex ReviewerIdFormat();

    public static void MapReviewEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/reviews").WithTags("Manual review")
            .RequireAuthorization(Policies.Partner)
            .RequireRateLimiting("partner")
            .AddEndpointFilter(RequireReviewer);

        group.MapGet("", GetQueue).WithSummary("List sessions waiting for manual review, oldest first");
        group.MapGet("/{sessionId:guid}", GetCase)
            .WithSummary("Open a review case: checks, extracted identity, short-lived image links, related sessions");
        group.MapPost("/{sessionId:guid}/decision", Decide).WithSummary("Approve or reject a session under manual review");
    }

    /// <summary>Every review call must name the reviewer; audit rows record "reviewer:&lt;id&gt; via partner:&lt;id&gt;".</summary>
    private static async ValueTask<object?> RequireReviewer(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var reviewerId = http.Request.Headers[ReviewerHeader].ToString();
        if (!ReviewerIdFormat().IsMatch(reviewerId))
            return Problems.Validation((ReviewerHeader, "Required: the reviewer's staff id (1-64 characters: letters, digits, . _ @ : -)."));

        http.Items[ReviewerHeader] = reviewerId;
        http.RequestServices.GetRequiredService<RequestContext>()
            .SetActor(ActorType.Reviewer, $"reviewer:{reviewerId} via partner:{http.User.PartnerId()}");
        return await next(ctx);
    }

    private static string ReviewerId(HttpContext http) => (string)http.Items[ReviewerHeader]!;

    private static async Task<IResult> GetQueue(ClaimsPrincipal user, KycDbContext db, int? limit, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 50, 1, 200);
        var partnerId = user.PartnerId();
        var rows = await db.Sessions.AsNoTracking()
            .Where(s => s.PartnerId == partnerId && s.Status == SessionStatus.ManualReview)
            .OrderBy(s => s.UpdatedAt)
            .Take(take)
            .Select(s => new
            {
                s.Id, s.ApplicantReference, s.UpdatedAt,
                Decision = s.Decisions.OrderByDescending(d => d.DecidedAt).FirstOrDefault(),
            })
            .ToListAsync(ct);
        return Results.Ok(rows.Select(r => new ReviewQueueItem(r.Id, r.ApplicantReference,
            r.Decision?.ReasonCodes ?? [], r.Decision?.RiskScore, r.UpdatedAt)));
    }

    private static async Task<IResult> GetCase(Guid sessionId, ClaimsPrincipal user, KycDbContext db,
        FieldProtector protector, IObjectStorage storage, IOptions<StorageOptions> storageOptions,
        TimeProvider clock, CancellationToken ct)
    {
        var session = await PartnerEndpoints.LoadVisibleSession(db, user, sessionId, ct);
        var now = clock.GetUtcNow();

        ExtractedIdentityView? identity = null;
        if (session.ExtractedIdentityEncrypted is { } encrypted)
        {
            var id = JsonSerializer.Deserialize<ExtractedIdentity>(protector.Unprotect(encrypted), KycDbContext.Json)!;
            identity = new ExtractedIdentityView(id.FullName, id.DateOfBirth, id.DocumentType,
                Mask(id.DocumentNumber), id.ExpiryDate, id.IssuingCountry);
        }

        var ttl = TimeSpan.FromSeconds(storageOptions.Value.DownloadUrlTtlSeconds);
        var images = new List<ArtifactLink>();
        foreach (var a in session.Artifacts.Where(a => a.Status == ArtifactStatus.Uploaded))
            images.Add(new ArtifactLink(a.Id, a.Type, (await storage.CreateDownloadUrlAsync(a.StorageKey, ttl)).ToString(),
                now.Add(ttl)));

        var related = session.DocumentNumberHash is null
            ? []
            : await db.Sessions.AsNoTracking()
                .Where(s => s.PartnerId == session.PartnerId && s.DocumentNumberHash == session.DocumentNumberHash
                            && s.Id != session.Id)
                .OrderByDescending(s => s.CreatedAt).Take(20)
                .Select(s => new RelatedSessionView(s.Id, s.Status, s.CreatedAt))
                .ToListAsync(ct);

        session.Record(AuditEventTypes.ArtifactsViewedByReviewer, new
        {
            artifactIds = images.Select(i => i.ArtifactId),
            identityRevealed = identity is not null,
        });
        await db.SaveChangesAsync(ct);

        return Results.Ok(new ReviewCaseView(session.ToView(now), identity, images, related));
    }

    private static async Task<IResult> Decide(Guid sessionId, ReviewDecisionRequest request, ClaimsPrincipal user,
        HttpContext http, KycDbContext db, TimeProvider clock, KycMetrics metrics, CancellationToken ct)
    {
        if (request.Outcome is not (DecisionOutcome.Approved or DecisionOutcome.Rejected))
            return Problems.Validation((nameof(request.Outcome), "Must be Approved or Rejected."));
        if (string.IsNullOrWhiteSpace(request.ReasonCode) || request.ReasonCode.Length > 64)
            return Problems.Validation((nameof(request.ReasonCode), "Required, at most 64 characters."));
        if (request.Notes?.Length > 2000)
            return Problems.Validation((nameof(request.Notes), "At most 2000 characters."));

        var session = await PartnerEndpoints.LoadVisibleSession(db, user, sessionId, ct);
        if (session.Status != SessionStatus.ManualReview)
            throw new SessionStateConflictException("not_in_review", $"Session is {session.Status}, not ManualReview.");

        var now = clock.GetUtcNow();
        session.ApplyDecision(Decision.Manual(session.Id, request.Outcome, ReviewerId(http),
            request.ReasonCode.Trim().ToUpperInvariant(), request.Notes, now), now);
        // xmin concurrency: if two reviewers decide at once, the second gets 409 concurrent_modification.
        await db.SaveChangesAsync(ct);
        metrics.Decision(request.Outcome.ToString(), nameof(ActorType.Reviewer));
        return Results.Ok(session.ToView(now));
    }

    private static string Mask(string value) =>
        value.Length <= 4 ? new string('*', value.Length) : new string('*', value.Length - 4) + value[^4..];
}
