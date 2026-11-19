namespace Kyc.Domain;

/// <summary>
/// Coarse lifecycle state of a verification session. Fine-grained step results live in
/// <see cref="ProviderCheck"/> rows; the status answers "what is the session waiting for?".
/// </summary>
public enum SessionStatus
{
    /// <summary>Session exists; waiting for the customer to upload an identity document.</summary>
    Created,
    /// <summary>Document uploaded; document validation is queued or running.</summary>
    DocumentSubmitted,
    /// <summary>Document accepted; waiting for the customer to upload a selfie.</summary>
    AwaitingSelfie,
    /// <summary>Selfie uploaded; liveness and face match are queued or running.</summary>
    SelfieSubmitted,
    /// <summary>All checks finished; risk assessment and decision are queued or running.</summary>
    PendingDecision,
    /// <summary>Automation could not decide; waiting for a human reviewer.</summary>
    ManualReview,
    Approved,
    Rejected,
    /// <summary>Customer abandoned the journey before completing it.</summary>
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
    /// <summary>Upload URL issued; bytes not yet confirmed in storage.</summary>
    PendingUpload,
    /// <summary>Upload confirmed by the client and verified against storage.</summary>
    Uploaded,
    /// <summary>Replaced by a newer upload of the same type (e.g. recapture).</summary>
    Superseded,
    /// <summary>Binary deleted from storage under the retention policy; metadata kept for audit.</summary>
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
    /// <summary>Definitive negative result (tampered document, spoof, different person).</summary>
    Failed,
    /// <summary>The image could not be processed (blur, glare, cropping). The customer may recapture.</summary>
    Unreadable,
    /// <summary>The provider returned a result, but not a confident one. Routed to a human.</summary>
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
    ApiClient,
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
