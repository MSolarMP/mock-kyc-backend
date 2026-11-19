namespace Kyc.Domain;

// What the session is waiting for. Per-check results live in ProviderCheck.
public enum SessionStatus
{
    // Waiting for the ID document.
    Created,
    DocumentSubmitted,
    AwaitingSelfie,
    SelfieSubmitted,
    PendingDecision,
    ManualReview,
    Approved,
    Rejected,
    // Customer abandoned the flow.
    Expired,
}

public enum Channel
{
    Web,
    MobileApp,
    MobileWeb,
}

public enum ArtifactType
{
    IdentityDocument,
    Selfie,
}

public enum ArtifactStatus
{
    // Upload URL issued, bytes not confirmed yet.
    PendingUpload,
    Uploaded,
    // Replaced by a newer upload, e.g. a recapture.
    Superseded,
    // File deleted by retention. Metadata kept for audit.
    Purged,
}

public enum CheckType
{
    DocumentValidation,
    Liveness,
    FaceMatch,
}

public enum CheckOutcome
{
    Passed,
    // Tampered document, spoof or different person.
    Failed,
    // Bad image (blur, glare). Customer can recapture.
    Unreadable,
    // Low-confidence result. Goes to a human.
    Inconclusive,
}

public enum DecisionOutcome
{
    Approved,
    Rejected,
    ManualReview,
}

public enum ActorType
{
    Customer,
    Partner,
    Reviewer,
    System,
}

public enum JobStatus
{
    Pending,
    Running,
    Completed,
    DeadLettered,
}

public enum JobType
{
    ValidateDocument,
    CheckLiveness,
    MatchFace,
    AssessRiskAndDecide,
}
