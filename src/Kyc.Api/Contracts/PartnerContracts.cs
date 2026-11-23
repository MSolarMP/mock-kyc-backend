using Kyc.Domain;

namespace Kyc.Api.Contracts;

// Partner (server-to-server) requests and views.

// Customer details passed on by the partner backend.
public sealed record CustomerContext(string? IpAddress, string? UserAgent, string? DeviceFingerprint);

public sealed record CreateSessionRequest(string ApplicantReference, Channel Channel, CustomerContext? Customer);

public sealed record CreateSessionResponse(
    Guid SessionId,
    SessionStatus Status,
    DateTimeOffset ExpiresAt,
    string ClientToken,
    DateTimeOffset ClientTokenExpiresAt);

public sealed record CheckView(
    CheckType Type, string Provider, string? ProviderReference, CheckOutcome Outcome, decimal? Score,
    IReadOnlyList<string> ReasonCodes, int Attempt, long DurationMs, DateTimeOffset CompletedAt);

public sealed record DecisionView(
    DecisionOutcome Outcome, ActorType DecidedBy, string? ReviewerId, IReadOnlyList<string> ReasonCodes,
    string? Notes, string? RulesVersion, int? RiskScore, DateTimeOffset DecidedAt);

public sealed record UploadedFileView(Guid Id, UploadedFileType Type, UploadedFileStatus Status, string ContentType,
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
    IReadOnlyList<UploadedFileView> UploadedFiles,
    IReadOnlyList<CheckView> Checks,
    IReadOnlyList<DecisionView> Decisions);

public sealed record AuditEventView(long Id, string EventType, ActorType ActorType, string? ActorId,
    string? CorrelationId, DateTimeOffset OccurredAt, System.Text.Json.JsonElement Data);
