using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kyc.Api.Auth;
using Kyc.Api.Contracts;
using Kyc.Api.Infrastructure;
using Kyc.Domain;
using Kyc.Infrastructure.Observability;
using Kyc.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Kyc.Api.Endpoints;

/// <summary>Server-to-server API for the partner's onboarding system.</summary>
public static class PartnerEndpoints
{
    public static void MapPartnerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/v1/verification-sessions").WithTags("Verification sessions (partner)")
            .RequireRateLimiting("partner");

        group.MapPost("", CreateSession).RequireAuthorization(Policies.Partner)
            .WithSummary("Start a verification session for an applicant")
            .WithDescription("Requires an Idempotency-Key header. Retrying with the same key and body returns the same session (with a fresh client token).");

        group.MapGet("/{sessionId:guid}", GetSession).RequireAuthorization(Policies.Partner)
            .WithSummary("Get the full status, check results and decisions of a session");

        group.MapGet("/{sessionId:guid}/events", GetAuditTrail).RequireAuthorization(Policies.Partner)
            .WithSummary("Get the append-only audit trail of a session");
    }

    private static async Task<IResult> CreateSession(
        CreateSessionRequest request,
        // Nullable so a missing header gets our validation error below; bound here so Swagger shows the field.
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        ClaimsPrincipal user,
        KycDbContext db,
        TimeProvider clock,
        KycMetrics metrics,
        IOptions<KycApiOptions> options,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(idempotencyKey) || idempotencyKey.Length > 128)
            return Problems.Validation(("Idempotency-Key", "Header is required (1-128 characters)."));
        if (string.IsNullOrWhiteSpace(request.ApplicantReference) || request.ApplicantReference.Length > 128)
            return Problems.Validation((nameof(request.ApplicantReference), "Required, at most 128 characters."));

        var partnerId = user.PartnerId();
        var requestHash = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request))));
        var now = clock.GetUtcNow();

        var existing = await FindByIdempotencyKey(db, partnerId, idempotencyKey, ct);
        if (existing is not null)
            return await Replay(existing, requestHash, request, db, now, ct);

        var customer = request.Customer;
        var session = VerificationSession.Create(partnerId, request.ApplicantReference.Trim(), request.Channel,
            idempotencyKey, requestHash, TimeSpan.FromMinutes(options.Value.SessionLifetimeMinutes), now,
            customer?.IpAddress, customer?.UserAgent, customer?.DeviceFingerprint);
        db.Sessions.Add(session);
        var (token, tokenEntity) = AccessTokens.Issue(db, session, request.Channel, viaHandoff: false,
            customer?.IpAddress, customer?.UserAgent, customer?.DeviceFingerprint, now);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Lost a race with a retry on the same key, so replay the winner's session.
            db.ChangeTracker.Clear();
            existing = await FindByIdempotencyKey(db, partnerId, idempotencyKey, ct);
            return await Replay(existing!, requestHash, request, db, now, ct);
        }

        metrics.SessionCreated(request.Channel.ToString());
        return Results.Created($"/v1/verification-sessions/{session.Id}",
            new CreateSessionResponse(session.Id, session.Status, session.ExpiresAt, token, tokenEntity.ExpiresAt));
    }

    private static Task<VerificationSession?> FindByIdempotencyKey(KycDbContext db, string partnerId, string key,
        CancellationToken ct) =>
        db.Sessions.SingleOrDefaultAsync(s => s.PartnerId == partnerId && s.IdempotencyKey == key, ct);

    private static async Task<IResult> Replay(VerificationSession existing, string requestHash,
        CreateSessionRequest request, KycDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        if (existing.IdempotencyRequestHash != requestHash)
            throw new BusinessRuleException("idempotency_key_reused",
                "This Idempotency-Key was already used with a different request body.");
        existing.EnsureUsableByCustomer(now);

        // Plaintext isn't stored, so a replay issues a fresh token. The old one stays valid.
        var (token, entity) = AccessTokens.Issue(db, existing, request.Channel, false,
            request.Customer?.IpAddress, request.Customer?.UserAgent, request.Customer?.DeviceFingerprint, now);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new CreateSessionResponse(existing.Id, existing.Status, existing.ExpiresAt, token, entity.ExpiresAt));
    }

    private static async Task<IResult> GetSession(Guid sessionId, ClaimsPrincipal user, KycDbContext db,
        TimeProvider clock, CancellationToken ct)
    {
        var session = await LoadVisibleSession(db, user, sessionId, ct);
        return Results.Ok(session.ToView(clock.GetUtcNow()));
    }

    private static async Task<IResult> GetAuditTrail(Guid sessionId, ClaimsPrincipal user, KycDbContext db,
        long? after, int? limit, CancellationToken ct)
    {
        await LoadVisibleSession(db, user, sessionId, ct, includeChildren: false);
        var take = Math.Clamp(limit ?? 100, 1, 500);
        var events = await db.AuditEvents.AsNoTracking()
            .Where(e => e.SessionId == sessionId && e.Id > (after ?? 0))
            .OrderBy(e => e.Id)
            .Take(take)
            .ToListAsync(ct);
        return Results.Ok(events.Select(e => new AuditEventView(e.Id, e.EventType, e.ActorType, e.ActorId,
            e.CorrelationId, e.OccurredAt, JsonDocument.Parse(e.Data).RootElement)));
    }

    /// <summary>Another partner's session returns 404, not 403, so ids can't be probed.</summary>
    internal static async Task<VerificationSession> LoadVisibleSession(KycDbContext db, ClaimsPrincipal user,
        Guid sessionId, CancellationToken ct, bool includeChildren = true)
    {
        var query = db.Sessions.AsQueryable();
        if (includeChildren)
            query = query.Include(s => s.Artifacts).Include(s => s.Checks).Include(s => s.Decisions).AsSplitQuery();
        var session = await query.SingleOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null || session.PartnerId != user.PartnerId())
            throw new NotFoundException("Verification session");
        return session;
    }
}
