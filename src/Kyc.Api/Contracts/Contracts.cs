using Kyc.Domain;

namespace Kyc.Api.Contracts;

// ---------- Partner (server-to-server) ----------

/// <summary>Context about the end customer, supplied by the partner backend (it sees the customer's request; we don't).</summary>
public sealed record CustomerContext(string? IpAddress, string? UserAgent, string? DeviceFingerprint);

public sealed record CreateSessionRequest(string ApplicantReference, Channel Channel, CustomerContext? Customer);

public sealed record CreateSessionResponse(
    Guid SessionId,
    SessionStatus Status,
    DateTimeOffset ExpiresAt,
    // Bearer token for the customer's device. Hand it to the front end; it is never retrievable again.
    string ClientToken,
    DateTimeOffset ClientTokenExpiresAt);

public sealed record CheckView(
    CheckType Type, string Provider, string? ProviderReference, CheckOutcome Outcome, decimal? Score,
    IReadOnlyList<string> ReasonCodes, int Attempt, long DurationMs, DateTimeOffset CompletedAt);

public sealed record DecisionView(
    DecisionOutcome Outcome, ActorType DecidedBy, string? ReviewerId, IReadOnlyList<string> ReasonCodes,
    string? Notes, string? RulesVersion, int? RiskScore, DateTimeOffset DecidedAt);

public sealed record ArtifactView(Guid Id, ArtifactType Type, ArtifactStatus Status, string ContentType,
    long SizeBytes, bool IntegrityVerified, DateTimeOffset? UploadedAt);

public sealed record SessionView(
    Guid Id,
    string ApplicantReference,
    Channel Channel,
    SessionStatus Status,
    string NextAction,
    int DocumentAttempts,
    int SelfieAttempts,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<ArtifactView> Artifacts,
    IReadOnlyList<CheckView> Checks,
    IReadOnlyList<DecisionView> Decisions);

public sealed record AuditEventView(long Id, string EventType, ActorType ActorType, string? ActorId,
    string? CorrelationId, DateTimeOffset OccurredAt, System.Text.Json.JsonElement Data);

// ---------- Customer device ----------

/// <summary>
/// What the customer's UI needs, and nothing more. Reason codes and scores are left out on purpose: telling a
/// fraudster *why* they failed helps them tune the next attempt.
/// </summary>
public sealed record ClientSessionView(
    SessionStatus Status,
    string NextAction,
    bool RecaptureRequested,
    int DocumentAttemptsRemaining,
    int SelfieAttemptsRemaining,
    DateTimeOffset ExpiresAt);

public sealed record CreateHandoffResponse(
    string HandoffUrl,
    DateTimeOffset ExpiresAt,
    // PNG of the QR code as base64, for convenience. Front ends can also render it from <see cref="HandoffUrl"/>.
    string QrCodePngBase64);

public sealed record RedeemHandoffRequest(string Token, string? DeviceFingerprint);

public sealed record RedeemHandoffResponse(string ClientToken, DateTimeOffset ClientTokenExpiresAt, ClientSessionView Session);

public sealed record CreateArtifactRequest(ArtifactType Type, string ContentType, long SizeBytes, string Sha256);

public sealed record CreateArtifactResponse(
    Guid ArtifactId,
    string UploadUrl,
    string UploadMethod,
    IReadOnlyDictionary<string, string> UploadHeaders,
    DateTimeOffset ExpiresAt);

// ---------- Reviewer ----------

public sealed record ReviewQueueItem(Guid SessionId, string ApplicantReference, string ClientId,
    IReadOnlyList<string> ReasonCodes, int? RiskScore, DateTimeOffset WaitingSince);

public sealed record ReviewCaseView(SessionView Session, ExtractedIdentityView? Identity,
    IReadOnlyList<ArtifactLink> Images, IReadOnlyList<RelatedSessionView> RelatedSessions);

public sealed record ExtractedIdentityView(string FullName, DateOnly DateOfBirth, string DocumentType,
    string DocumentNumberMasked, DateOnly? ExpiryDate, string IssuingCountry);

public sealed record ArtifactLink(Guid ArtifactId, ArtifactType Type, string Url, DateTimeOffset ExpiresAt);

/// <summary>Other sessions that share the same document (by HMAC). Investigators use this to spot repeat attempts and synthetic identities.</summary>
public sealed record RelatedSessionView(Guid SessionId, string ClientId, SessionStatus Status, DateTimeOffset CreatedAt);

public sealed record ReviewDecisionRequest(DecisionOutcome Outcome, string ReasonCode, string? Notes);

public static class NextActions
{
    public const string UploadDocument = "UPLOAD_DOCUMENT";
    public const string UploadSelfie = "UPLOAD_SELFIE";
    public const string Wait = "WAIT";
    public const string Complete = "COMPLETE";
    public const string Expired = "EXPIRED";

    public static string For(VerificationSession s, DateTimeOffset now) => s.Status switch
    {
        _ when s.HasLapsed(now) => Expired,
        SessionStatus.Created => UploadDocument,
        SessionStatus.AwaitingSelfie => UploadSelfie,
        SessionStatus.Approved or SessionStatus.Rejected => Complete,
        SessionStatus.Expired => Expired,
        _ => Wait, // processing, or with a human reviewer
    };
}

public static class Mapping
{
    public static SessionView ToView(this VerificationSession s, DateTimeOffset now) => new(
        s.Id, s.ApplicantReference, s.Channel, s.Status, NextActions.For(s, now),
        s.DocumentAttempts, s.SelfieAttempts, s.CreatedAt, s.UpdatedAt, s.ExpiresAt, s.CompletedAt,
        s.Artifacts.OrderBy(a => a.CreatedAt)
            .Select(a => new ArtifactView(a.Id, a.Type, a.Status, a.ContentType, a.DeclaredSizeBytes,
                a.IntegrityVerified, a.UploadedAt)).ToList(),
        s.Checks.OrderBy(c => c.CompletedAt)
            .Select(c => new CheckView(c.Type, c.Provider, c.ProviderReference, c.Outcome, c.Score, c.ReasonCodes,
                c.Attempt, c.DurationMs, c.CompletedAt)).ToList(),
        s.Decisions.OrderBy(d => d.DecidedAt)
            .Select(d => new DecisionView(d.Outcome, d.DecidedBy, d.ReviewerId, d.ReasonCodes, d.Notes,
                d.RulesVersion, d.RiskScore, d.DecidedAt)).ToList());

    public static ClientSessionView ToClientView(this VerificationSession s, DateTimeOffset now) => new(
        s.HasLapsed(now) ? SessionStatus.Expired : s.Status,
        NextActions.For(s, now),
        RecaptureRequested: (s.Status == SessionStatus.Created && s.DocumentAttempts > 0)
                            || (s.Status == SessionStatus.AwaitingSelfie && s.SelfieAttempts > 0),
        VerificationSession.MaxDocumentAttempts - s.DocumentAttempts,
        VerificationSession.MaxSelfieAttempts - s.SelfieAttempts,
        s.ExpiresAt);
}
