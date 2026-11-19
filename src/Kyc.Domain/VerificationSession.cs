namespace Kyc.Domain;

/// <summary>
/// Aggregate root for one verification attempt. State changes go through methods on this class,
/// which enforce <see cref="SessionStateMachine"/> and record a <see cref="SessionEvent"/>.
/// The persistence layer turns those events into append-only audit rows in the
/// same transaction as the state change, so the audit trail can't drift from the data.
/// </summary>
public class VerificationSession
{
    public const int MaxDocumentAttempts = 3;
    public const int MaxSelfieAttempts = 3;

    private readonly List<SessionEvent> _pendingEvents = [];

    private VerificationSession() { } // EF

    public Guid Id { get; private set; }

    /// <summary>The calling system's reference for the applicant (e.g. its customer/application id). Not PII.</summary>
    public string ApplicantReference { get; private set; } = default!;

    /// <summary>The partner (tenant) that created the session. Partners can only see their own sessions.</summary>
    public string PartnerId { get; private set; } = default!;

    public Channel Channel { get; private set; }
    public SessionStatus Status { get; private set; }

    public int DocumentAttempts { get; private set; }
    public int SelfieAttempts { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>Idempotency key supplied on creation, unique per partner. A retried create returns the same session.</summary>
    public string IdempotencyKey { get; private set; } = default!;
    public string IdempotencyRequestHash { get; private set; } = default!;

    // ---- Fraud / investigation signals (captured now so detection can be added later) ----
    public string? CreatedIp { get; private set; }
    public string? CreatedUserAgent { get; private set; }
    public string? DeviceFingerprint { get; private set; }

    /// <summary>
    /// Keyed HMAC of the document number. Lets us find "same identity document, different session"
    /// (repeat attempts after a rejection, synthetic identities) without storing the number in plaintext.
    /// </summary>
    public string? DocumentNumberHash { get; private set; }

    /// <summary>Data extracted from the document (name, DOB, ...), encrypted at the field level.</summary>
    public byte[]? ExtractedIdentityEncrypted { get; private set; }

    /// <summary>Optimistic concurrency token (Postgres xmin). Two devices writing the same session can't overwrite each other.</summary>
    public uint Version { get; private set; }

    public List<Artifact> Artifacts { get; private set; } = [];
    public List<ProviderCheck> Checks { get; private set; } = [];
    public List<Decision> Decisions { get; private set; } = [];

    public IReadOnlyList<SessionEvent> PendingEvents => _pendingEvents;

    public static VerificationSession Create(
        string partnerId,
        string applicantReference,
        Channel channel,
        string idempotencyKey,
        string idempotencyRequestHash,
        TimeSpan lifetime,
        DateTimeOffset now,
        string? ip,
        string? userAgent,
        string? deviceFingerprint)
    {
        var session = new VerificationSession
        {
            Id = Guid.CreateVersion7(now),
            PartnerId = partnerId,
            ApplicantReference = applicantReference,
            Channel = channel,
            Status = SessionStatus.Created,
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = now.Add(lifetime),
            IdempotencyKey = idempotencyKey,
            IdempotencyRequestHash = idempotencyRequestHash,
            CreatedIp = ip,
            CreatedUserAgent = userAgent,
            DeviceFingerprint = deviceFingerprint,
        };
        session.Record(AuditEventTypes.SessionCreated, new
        {
            channel = channel.ToString(),
            expiresAt = session.ExpiresAt,
        });
        return session;
    }

    public bool IsTerminal => SessionStateMachine.IsTerminal(Status);

    public bool HasLapsed(DateTimeOffset now) =>
        SessionStateMachine.IsAwaitingCustomer(Status) && now >= ExpiresAt;

    /// <summary>Guard for customer-facing operations.</summary>
    public void EnsureUsableByCustomer(DateTimeOffset now)
    {
        if (Status == SessionStatus.Expired || HasLapsed(now))
            throw new SessionExpiredException();
    }

    public void TransitionTo(SessionStatus target, DateTimeOffset now, string reason)
    {
        if (!SessionStateMachine.CanTransition(Status, target))
            throw new InvalidStateTransitionException(Status, target);

        var from = Status;
        Status = target;
        UpdatedAt = now;
        if (SessionStateMachine.IsTerminal(target))
            CompletedAt = now;

        Record(AuditEventTypes.StatusChanged, new { from = from.ToString(), to = target.ToString(), reason });
    }

    /// <summary>Called when the customer confirms an upload. Advances the workflow and counts the attempt.</summary>
    public void SubmitArtifact(Artifact artifact, DateTimeOffset now)
    {
        switch (artifact.Type)
        {
            case ArtifactType.IdentityDocument:
                if (DocumentAttempts >= MaxDocumentAttempts)
                    throw new SessionStateConflictException("attempts_exhausted", "No more document attempts are allowed.");
                DocumentAttempts++;
                TransitionTo(SessionStatus.DocumentSubmitted, now, "document_uploaded");
                break;
            case ArtifactType.Selfie:
                if (SelfieAttempts >= MaxSelfieAttempts)
                    throw new SessionStateConflictException("attempts_exhausted", "No more selfie attempts are allowed.");
                SelfieAttempts++;
                TransitionTo(SessionStatus.SelfieSubmitted, now, "selfie_uploaded");
                break;
        }
    }

    /// <summary>Which artifact type the session will accept right now, if any.</summary>
    public ArtifactType? ExpectedArtifact => Status switch
    {
        SessionStatus.Created => ArtifactType.IdentityDocument,
        SessionStatus.AwaitingSelfie => ArtifactType.Selfie,
        _ => null,
    };

    public Artifact AddArtifact(ArtifactType type, string contentType, long sizeBytes, string sha256,
        Guid uploadedByTokenId, DateTimeOffset now)
    {
        if (ExpectedArtifact != type)
            throw new SessionStateConflictException("unexpected_artifact",
                $"A {type} upload is not expected while the session is {Status}.");

        // Supersede any earlier upload of the same type that never completed.
        foreach (var stale in Artifacts.Where(a => a.Type == type && a.Status == ArtifactStatus.PendingUpload))
            stale.MarkSuperseded();

        var artifact = Artifact.Create(Id, type, contentType, sizeBytes, sha256, uploadedByTokenId, now);
        Artifacts.Add(artifact);
        UpdatedAt = now;
        Record(AuditEventTypes.ArtifactUploadRequested, new
        {
            artifactId = artifact.Id,
            type = type.ToString(),
            contentType,
            sizeBytes,
        });
        return artifact;
    }

    public void RecordExtractedIdentity(string documentNumberHash, byte[] encryptedIdentity)
    {
        DocumentNumberHash = documentNumberHash;
        ExtractedIdentityEncrypted = encryptedIdentity;
    }

    public void ApplyDecision(Decision decision, DateTimeOffset now)
    {
        Decisions.Add(decision);
        var target = decision.Outcome switch
        {
            DecisionOutcome.Approved => SessionStatus.Approved,
            DecisionOutcome.Rejected => SessionStatus.Rejected,
            _ => SessionStatus.ManualReview,
        };
        Record(AuditEventTypes.DecisionMade, new
        {
            decisionId = decision.Id,
            outcome = decision.Outcome.ToString(),
            decidedBy = decision.DecidedBy.ToString(),
            reviewerId = decision.ReviewerId,
            reasons = decision.ReasonCodes,
            rulesVersion = decision.RulesVersion,
            riskScore = decision.RiskScore,
        });
        TransitionTo(target, now, $"decision:{decision.Outcome}");
    }

    public void Record(string eventType, object? data = null) =>
        _pendingEvents.Add(new SessionEvent(Id, eventType, data));

    public void ClearPendingEvents() => _pendingEvents.Clear();
}

/// <summary>Something that happened to a session, waiting to be persisted as an audit event.</summary>
public sealed record SessionEvent(Guid SessionId, string EventType, object? Data);
