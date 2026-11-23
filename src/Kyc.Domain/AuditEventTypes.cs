namespace Kyc.Domain;

// Reports and fraud tooling query these names, so don't rename them.
public static class AuditEventTypes
{
    public const string SessionCreated = "session.created";
    public const string StatusChanged = "session.status_changed";
    public const string HandoffIssued = "handoff.issued";
    public const string HandoffRedeemed = "handoff.redeemed";
    public const string HandoffRedeemFailed = "handoff.redeem_failed";
    public const string UploadRequested = "upload.requested";
    public const string UploadCompleted = "upload.completed";
    public const string UploadIntegrityFailed = "upload.integrity_failed";
    public const string UploadPurged = "upload.purged";
    public const string UploadsViewedByReviewer = "upload.viewed_by_reviewer";
    public const string CheckCompleted = "check.completed";
    public const string CheckProviderUnavailable = "check.provider_unavailable";
    public const string RecaptureRequested = "session.recapture_requested";
    public const string RiskAssessed = "risk.assessed";
    public const string DecisionMade = "decision.made";
}
