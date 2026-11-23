namespace Kyc.Domain;

// All state changes go through here. Events are saved as audit rows in the same transaction.
public class VerificationSession
{
    public const int MaxDocumentAttempts = 3;
    public const int MaxSelfieAttempts = 3;

    private readonly List<SessionEvent> _pendingEvents = [];

    private VerificationSession() { } // EF

    public Guid Id { get; private set; }

    // The partner's own id for the applicant.
    public string ApplicantReference { get; private set; } = default!;

    public string PartnerId { get; private set; } = default!;

    public Channel Channel { get; private set; }
    public SessionStatus Status { get; private set; }

    public int DocumentAttempts { get; private set; }
    public int SelfieAttempts { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    // Unique per partner. A retried create returns the same session.
    public string IdempotencyKey { get; private set; } = default!;
    public string IdempotencyRequestHash { get; private set; } = default!;

    // Fraud signals.
    public string? CreatedIp { get; private set; }
    public string? CreatedUserAgent { get; private set; }
    public string? DeviceFingerprint { get; private set; }

    // Keyed HMAC of the document number.
    public string? DocumentNumberHash { get; private set; }

    // Name, DOB etc. Encrypted at field level.
    public byte[]? ExtractedIdentityEncrypted { get; private set; }

    // Postgres xmin, for optimistic concurrency.
    public uint Version { get; private set; }

    public List<UploadedFile> UploadedFiles { get; private set; } = [];
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

    public void SubmitUploadedFile(UploadedFile uploadedFile, DateTimeOffset now)
    {
        switch (uploadedFile.Type)
        {
            case UploadedFileType.IdentityDocument:
                if (DocumentAttempts >= MaxDocumentAttempts)
                    throw new SessionStateConflictException("attempts_exhausted", "No more document attempts are allowed.");
                DocumentAttempts++;
                TransitionTo(SessionStatus.DocumentSubmitted, now, "document_uploaded");
                break;
            case UploadedFileType.Selfie:
                if (SelfieAttempts >= MaxSelfieAttempts)
                    throw new SessionStateConflictException("attempts_exhausted", "No more selfie attempts are allowed.");
                SelfieAttempts++;
                TransitionTo(SessionStatus.SelfieSubmitted, now, "selfie_uploaded");
                break;
        }
    }

    public UploadedFileType? ExpectedUploadType => Status switch
    {
        SessionStatus.Created => UploadedFileType.IdentityDocument,
        SessionStatus.AwaitingSelfie => UploadedFileType.Selfie,
        _ => null,
    };

    public UploadedFile AddUploadedFile(UploadedFileType type, string contentType, long sizeBytes, string sha256,
        Guid uploadedByTokenId, DateTimeOffset now)
    {
        if (ExpectedUploadType != type)
            throw new SessionStateConflictException("unexpected_upload",
                $"A {type} upload is not expected while the session is {Status}.");

        // Drop any earlier upload of this type that never finished.
        foreach (var stale in UploadedFiles.Where(a => a.Type == type && a.Status == UploadedFileStatus.PendingUpload))
            stale.MarkSuperseded();

        var uploadedFile = UploadedFile.Create(Id, type, contentType, sizeBytes, sha256, uploadedByTokenId, now);
        UploadedFiles.Add(uploadedFile);
        UpdatedAt = now;
        Record(AuditEventTypes.UploadRequested, new
        {
            uploadedFileId = uploadedFile.Id,
            type = type.ToString(),
            contentType,
            sizeBytes,
        });
        return uploadedFile;
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

// Becomes an audit event on save.
public sealed record SessionEvent(Guid SessionId, string EventType, object? Data);
