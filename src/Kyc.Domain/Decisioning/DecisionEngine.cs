namespace Kyc.Domain.Decisioning;

/// <summary>Summary of one provider check, as the decision engine sees it.</summary>
public sealed record CheckSummary(CheckOutcome Outcome, decimal? Score, IReadOnlyList<string> ReasonCodes);

/// <summary>
/// Signals taken from history across sessions. This is where fraud detection hooks in: today a few simple
/// counts; later, scores from a device-intelligence or fraud vendor can be added to this record.
/// </summary>
public sealed record RiskSignals(
    int PriorRejectionsForSameDocument,
    int OpenSessionsForSameDocument,
    int SessionsFromSameIpLast24h,
    int SelfieReusedInOtherSessions,
    bool DocumentExpired);

public sealed record DecisionInput(
    CheckSummary? Document,
    CheckSummary? Liveness,
    CheckSummary? FaceMatch,
    RiskSignals Signals);

public sealed record DecisionResult(
    DecisionOutcome Outcome,
    IReadOnlyList<string> ReasonCodes,
    int RiskScore,
    string RulesVersion);

/// <summary>Thresholds come from configuration so risk/compliance can tune them without a code change.</summary>
public sealed record DecisionPolicy
{
    public decimal FaceMatchApproveThreshold { get; init; } = 0.85m;
    public decimal FaceMatchRejectThreshold { get; init; } = 0.60m;
    public decimal LivenessApproveThreshold { get; init; } = 0.80m;
    public int MaxSessionsPerIpPerDay { get; init; } = 10;
    public string RulesVersion { get; init; } = "rules-2026.11.1";
}

/// <summary>
/// Turns check results and risk signals into an outcome. Pure and deterministic: the same input always gives
/// the same output, and every outcome comes with machine-readable reason codes for auditors and reviewers.
/// Order of precedence: any hard-reject rule beats manual review, which beats approval.
/// </summary>
public sealed class DecisionEngine(DecisionPolicy policy)
{
    public DecisionPolicy Policy { get; } = policy;

    public DecisionResult Decide(DecisionInput input)
    {
        var reject = new List<string>();
        var review = new List<string>();
        var risk = 0;

        // --- Document ---
        switch (input.Document?.Outcome)
        {
            case null:
                review.Add(ReasonCodes.DocumentCheckMissing); risk += 30; break;
            case CheckOutcome.Failed:
                reject.Add(ReasonCodes.DocumentFailed); risk += 60; break;
            case CheckOutcome.Unreadable:
                review.Add(ReasonCodes.DocumentUnreadable); risk += 20; break;
            case CheckOutcome.Inconclusive:
                review.Add(ReasonCodes.DocumentInconclusive); risk += 20; break;
        }
        if (input.Signals.DocumentExpired)
        {
            reject.Add(ReasonCodes.DocumentExpired); risk += 40;
        }

        // --- Liveness ---
        // An "Unreadable" liveness result only reaches the engine once the customer has used up their recaptures.
        switch (input.Liveness?.Outcome)
        {
            case null when input.Document?.Outcome is CheckOutcome.Failed:
                break; // Liveness is skipped once the document has hard-failed; that isn't a gap.
            case null:
                review.Add(ReasonCodes.LivenessCheckMissing); risk += 30; break;
            case CheckOutcome.Failed:
                reject.Add(ReasonCodes.LivenessFailed); risk += 60; break;
            case CheckOutcome.Unreadable:
                review.Add(ReasonCodes.LivenessUnreadable); risk += 20; break;
            case CheckOutcome.Inconclusive:
                review.Add(ReasonCodes.LivenessInconclusive); risk += 20; break;
            case CheckOutcome.Passed when input.Liveness.Score < Policy.LivenessApproveThreshold:
                review.Add(ReasonCodes.LivenessLowConfidence); risk += 15; break;
        }

        // --- Face match ---
        var faceSkipped = input.Document?.Outcome is CheckOutcome.Failed
                          || input.Liveness?.Outcome is CheckOutcome.Failed or CheckOutcome.Unreadable;
        if (input.FaceMatch is null)
        {
            if (!faceSkipped) { review.Add(ReasonCodes.FaceMatchMissing); risk += 30; }
        }
        else if (input.FaceMatch.Outcome == CheckOutcome.Failed
                 || input.FaceMatch.Score is { } low && low < Policy.FaceMatchRejectThreshold)
        {
            reject.Add(ReasonCodes.FaceMismatch); risk += 60;
        }
        else if (input.FaceMatch.Outcome != CheckOutcome.Passed
                 || input.FaceMatch.Score is not { } s || s < Policy.FaceMatchApproveThreshold)
        {
            review.Add(ReasonCodes.FaceMatchLowConfidence); risk += 20;
        }

        // --- Cross-session fraud signals: send to a human, never auto-reject on these alone ---
        if (input.Signals.PriorRejectionsForSameDocument > 0)
        {
            review.Add(ReasonCodes.RepeatAttemptAfterRejection); risk += 25;
        }
        if (input.Signals.OpenSessionsForSameDocument > 0)
        {
            review.Add(ReasonCodes.ConcurrentSessionsSameDocument); risk += 15;
        }
        if (input.Signals.SessionsFromSameIpLast24h > Policy.MaxSessionsPerIpPerDay)
        {
            review.Add(ReasonCodes.HighIpVelocity); risk += 15;
        }
        if (input.Signals.SelfieReusedInOtherSessions > 0)
        {
            review.Add(ReasonCodes.SelfieReused); risk += 40;
        }

        risk = Math.Min(risk, 100);

        if (reject.Count > 0)
            return new DecisionResult(DecisionOutcome.Rejected, [.. reject, .. review], risk, Policy.RulesVersion);
        if (review.Count > 0)
            return new DecisionResult(DecisionOutcome.ManualReview, review, risk, Policy.RulesVersion);
        return new DecisionResult(DecisionOutcome.Approved, [ReasonCodes.AllChecksPassed], risk, Policy.RulesVersion);
    }
}

public static class ReasonCodes
{
    public const string AllChecksPassed = "ALL_CHECKS_PASSED";
    public const string DocumentCheckMissing = "DOCUMENT_CHECK_MISSING";
    public const string DocumentFailed = "DOCUMENT_FAILED";
    public const string DocumentUnreadable = "DOCUMENT_UNREADABLE";
    public const string DocumentInconclusive = "DOCUMENT_INCONCLUSIVE";
    public const string DocumentExpired = "DOCUMENT_EXPIRED";
    public const string LivenessCheckMissing = "LIVENESS_CHECK_MISSING";
    public const string LivenessFailed = "LIVENESS_FAILED";
    public const string LivenessUnreadable = "LIVENESS_UNREADABLE";
    public const string LivenessInconclusive = "LIVENESS_INCONCLUSIVE";
    public const string LivenessLowConfidence = "LIVENESS_LOW_CONFIDENCE";
    public const string FaceMatchMissing = "FACE_MATCH_MISSING";
    public const string FaceMismatch = "FACE_MISMATCH";
    public const string FaceMatchLowConfidence = "FACE_MATCH_LOW_CONFIDENCE";
    public const string RepeatAttemptAfterRejection = "REPEAT_ATTEMPT_AFTER_REJECTION";
    public const string ConcurrentSessionsSameDocument = "CONCURRENT_SESSIONS_SAME_DOCUMENT";
    public const string HighIpVelocity = "HIGH_IP_VELOCITY";
    public const string SelfieReused = "SELFIE_REUSED";
    public const string ProviderUnavailable = "PROVIDER_UNAVAILABLE";
    public const string ArtifactIntegrityMismatch = "ARTIFACT_INTEGRITY_MISMATCH";
}
