namespace Kyc.Domain;

/// <summary>Stable event names. Treat them as a public contract: compliance reports and fraud tooling query on them.</summary>
public static class AuditEventTypes
{
    public const string SessionCreated = "session.created";
    public const string StatusChanged = "session.status_changed";
    public const string SessionViewed = "session.viewed";
    public const string HandoffIssued = "handoff.issued";
    public const string HandoffRedeemed = "handoff.redeemed";
    public const string HandoffRedeemFailed = "handoff.redeem_failed";
    public const string ArtifactUploadRequested = "artifact.upload_requested";
    public const string ArtifactUploaded = "artifact.uploaded";
    public const string ArtifactIntegrityFailed = "artifact.integrity_failed";
    public const string ArtifactPurged = "artifact.purged";
    public const string ArtifactsViewedByReviewer = "artifact.viewed_by_reviewer";
    public const string CheckCompleted = "check.completed";
    public const string CheckProviderUnavailable = "check.provider_unavailable";
    public const string RecaptureRequested = "session.recapture_requested";
    public const string RiskAssessed = "risk.assessed";
    public const string DecisionMade = "decision.made";
}
