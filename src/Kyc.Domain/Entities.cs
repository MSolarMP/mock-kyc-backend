namespace Kyc.Domain;

/// <summary>An uploaded binary (identity document or selfie). Only metadata is stored here; the bytes live in object storage.</summary>
public class Artifact
{
    private Artifact() { }

    public Guid Id { get; private set; }
    public Guid SessionId { get; private set; }
    public ArtifactType Type { get; private set; }
    public ArtifactStatus Status { get; private set; }
    public string StorageKey { get; private set; } = default!;
    public string ContentType { get; private set; } = default!;
    public long DeclaredSizeBytes { get; private set; }
    /// <summary>SHA-256 the client declared. It is re-checked against the stored bytes before vendors see the file.</summary>
    public string DeclaredSha256 { get; private set; } = default!;
    public bool IntegrityVerified { get; private set; }
    /// <summary>The access token (and so the device) that performed the upload, kept for investigations.</summary>
    public Guid UploadedByTokenId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? UploadedAt { get; private set; }
    public DateTimeOffset? PurgedAt { get; private set; }

    internal static Artifact Create(Guid sessionId, ArtifactType type, string contentType, long size, string sha256,
        Guid tokenId, DateTimeOffset now)
    {
        var id = Guid.CreateVersion7(now);
        var ext = contentType == "image/png" ? "png" : "jpg";
        return new Artifact
        {
            Id = id,
            SessionId = sessionId,
            Type = type,
            Status = ArtifactStatus.PendingUpload,
            // The key contains no PII. Session-prefixed so retention jobs and investigators can list a session's files.
            StorageKey = $"sessions/{sessionId:N}/{type.ToString().ToLowerInvariant()}/{id:N}.{ext}",
            ContentType = contentType,
            DeclaredSizeBytes = size,
            DeclaredSha256 = sha256.ToLowerInvariant(),
            UploadedByTokenId = tokenId,
            CreatedAt = now,
        };
    }

    public void MarkUploaded(DateTimeOffset now)
    {
        if (Status != ArtifactStatus.PendingUpload)
            throw new SessionStateConflictException("artifact_not_pending", $"Artifact is {Status}.");
        Status = ArtifactStatus.Uploaded;
        UploadedAt = now;
    }

    public void MarkIntegrityVerified() => IntegrityVerified = true;
    public void MarkSuperseded() => Status = ArtifactStatus.Superseded;

    public void MarkPurged(DateTimeOffset now)
    {
        Status = ArtifactStatus.Purged;
        PurgedAt = now;
    }
}

/// <summary>One call to an external verification provider and what it returned.</summary>
public class ProviderCheck
{
    private ProviderCheck() { }

    public Guid Id { get; private set; }
    public Guid SessionId { get; private set; }
    public Guid? ArtifactId { get; private set; }
    public CheckType Type { get; private set; }
    /// <summary>The provider that did the check (e.g. "vendor-a"). Results stay comparable when vendors change.</summary>
    public string Provider { get; private set; } = default!;
    public string? ProviderReference { get; private set; }
    public CheckOutcome Outcome { get; private set; }
    /// <summary>Normalised confidence between 0 and 1, whatever scale the provider uses.</summary>
    public decimal? Score { get; private set; }
    public List<string> ReasonCodes { get; private set; } = [];
    public int Attempt { get; private set; }
    public long DurationMs { get; private set; }
    public DateTimeOffset CompletedAt { get; private set; }

    public static ProviderCheck Create(Guid sessionId, Guid? artifactId, CheckType type, string provider,
        string? providerReference, CheckOutcome outcome, decimal? score, IEnumerable<string> reasonCodes,
        int attempt, long durationMs, DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(now),
            SessionId = sessionId,
            ArtifactId = artifactId,
            Type = type,
            Provider = provider,
            ProviderReference = providerReference,
            Outcome = outcome,
            Score = score,
            ReasonCodes = reasonCodes.ToList(),
            Attempt = attempt,
            DurationMs = durationMs,
            CompletedAt = now,
        };
}

/// <summary>
/// A decision about a session. There can be more than one row: the system may decide ManualReview, and a
/// reviewer then decides Approved or Rejected. Keeping both shows whether a human stepped in, and on what grounds.
/// </summary>
public class Decision
{
    private Decision() { }

    public Guid Id { get; private set; }
    public Guid SessionId { get; private set; }
    public DecisionOutcome Outcome { get; private set; }
    public ActorType DecidedBy { get; private set; }
    public string? ReviewerId { get; private set; }
    public List<string> ReasonCodes { get; private set; } = [];
    public string? Notes { get; private set; }
    /// <summary>The ruleset version that produced an automated decision. Lets you replay decisions after rules change.</summary>
    public string? RulesVersion { get; private set; }
    public int? RiskScore { get; private set; }
    public DateTimeOffset DecidedAt { get; private set; }

    public static Decision Automated(Guid sessionId, DecisionOutcome outcome, IEnumerable<string> reasons,
        string rulesVersion, int riskScore, DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(now),
            SessionId = sessionId,
            Outcome = outcome,
            DecidedBy = ActorType.System,
            ReasonCodes = reasons.ToList(),
            RulesVersion = rulesVersion,
            RiskScore = riskScore,
            DecidedAt = now,
        };

    public static Decision Manual(Guid sessionId, DecisionOutcome outcome, string reviewerId, string reasonCode,
        string? notes, DateTimeOffset now)
    {
        if (outcome == DecisionOutcome.ManualReview)
            throw new BusinessRuleException("invalid_outcome", "A reviewer must approve or reject.");
        return new Decision
        {
            Id = Guid.CreateVersion7(now),
            SessionId = sessionId,
            Outcome = outcome,
            DecidedBy = ActorType.Reviewer,
            ReviewerId = reviewerId,
            ReasonCodes = [reasonCode],
            Notes = notes,
            DecidedAt = now,
        };
    }
}

/// <summary>A short-lived, single-use token that moves a session from one device to another (encoded in the QR code).</summary>
public class HandoffToken
{
    private HandoffToken() { }

    public Guid Id { get; private set; }
    public Guid SessionId { get; private set; }
    /// <summary>SHA-256 of the token. The plaintext is never stored, so a database leak can't be replayed.</summary>
    public string TokenHash { get; private set; } = default!;
    public Guid IssuedByTokenId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? RedeemedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public string? RedeemedIp { get; private set; }
    public string? RedeemedUserAgent { get; private set; }

    public static HandoffToken Create(Guid sessionId, string tokenHash, Guid issuedBy, TimeSpan ttl, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        SessionId = sessionId,
        TokenHash = tokenHash,
        IssuedByTokenId = issuedBy,
        CreatedAt = now,
        ExpiresAt = now.Add(ttl),
    };

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;
}

/// <summary>
/// An opaque bearer token that lets one device act on one session. Opaque rather than a JWT so it can be
/// revoked at once, and because the session id comes from the token, a customer can't reach another customer's session.
/// </summary>
public class SessionAccessToken
{
    private SessionAccessToken() { }

    public Guid Id { get; private set; }
    public Guid SessionId { get; private set; }
    public string TokenHash { get; private set; } = default!;
    public Channel Channel { get; private set; }
    /// <summary>True when the token came from redeeming a QR handoff rather than from session creation.</summary>
    public bool ViaHandoff { get; private set; }
    public string? IpAddress { get; private set; }
    public string? UserAgent { get; private set; }
    public string? DeviceFingerprint { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    public static SessionAccessToken Create(Guid sessionId, string tokenHash, Channel channel, bool viaHandoff,
        string? ip, string? userAgent, string? fingerprint, DateTimeOffset expiresAt, DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(now),
            SessionId = sessionId,
            TokenHash = tokenHash,
            Channel = channel,
            ViaHandoff = viaHandoff,
            IpAddress = ip,
            UserAgent = userAgent,
            DeviceFingerprint = fingerprint,
            CreatedAt = now,
            ExpiresAt = expiresAt,
        };

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && now < ExpiresAt;
}

/// <summary>Append-only audit record. A database trigger blocks UPDATE and DELETE on this table.</summary>
public class AuditEvent
{
    public long Id { get; set; }
    public Guid SessionId { get; set; }
    public string EventType { get; set; } = default!;
    public ActorType ActorType { get; set; }
    public string? ActorId { get; set; }
    public string? CorrelationId { get; set; }
    public string? IpAddress { get; set; }
    /// <summary>JSON payload. Must not contain raw PII: use ids, codes and hashes.</summary>
    public string Data { get; set; } = "{}";
    public DateTimeOffset OccurredAt { get; set; }
}

/// <summary>
/// A durable unit of workflow work. Written in the same transaction as the state change that causes it,
/// so a crash can't leave a session stuck with no work queued.
/// </summary>
public class WorkflowJob
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public JobType Type { get; set; }
    public string Payload { get; set; } = "{}";
    public JobStatus Status { get; set; }
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; }
    public DateTimeOffset RunAfter { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public string? LockedBy { get; set; }
    public string? LastError { get; set; }
    public string? CorrelationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public static WorkflowJob Create(Guid sessionId, JobType type, string payload, string? correlationId,
        DateTimeOffset now, int maxAttempts = 5) => new()
        {
            Id = Guid.CreateVersion7(now),
            SessionId = sessionId,
            Type = type,
            Payload = payload,
            Status = JobStatus.Pending,
            MaxAttempts = maxAttempts,
            RunAfter = now,
            CorrelationId = correlationId,
            CreatedAt = now,
        };
}
