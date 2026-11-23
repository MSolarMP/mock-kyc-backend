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
