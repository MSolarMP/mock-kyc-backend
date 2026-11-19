using Kyc.Domain;
using Kyc.Domain.Decisioning;

namespace Kyc.UnitTests;

public class DecisionEngineTests
{
    private readonly DecisionEngine _engine = new(new DecisionPolicy());

    private static CheckSummary Pass(decimal score = 0.97m) => new(CheckOutcome.Passed, score, []);
    private static RiskSignals Clean => new(0, 0, 1, 0, false);

    private static DecisionInput Input(CheckSummary? doc = null, CheckSummary? live = null, CheckSummary? face = null,
        RiskSignals? signals = null, bool skipDefaults = false) =>
        new(skipDefaults ? doc : doc ?? Pass(), skipDefaults ? live : live ?? Pass(),
            skipDefaults ? face : face ?? Pass(), signals ?? Clean);

    [Fact]
    public void Approves_when_all_checks_pass_and_no_risk_signals()
    {
        var result = _engine.Decide(Input());
        Assert.Equal(DecisionOutcome.Approved, result.Outcome);
        Assert.Equal([ReasonCodes.AllChecksPassed], result.ReasonCodes);
        Assert.Equal("rules-2026.11.1", result.RulesVersion);
    }

    [Fact]
    public void Rejects_tampered_document()
    {
        var result = _engine.Decide(Input(doc: new CheckSummary(CheckOutcome.Failed, 0.1m, ["TAMPERING"]),
            live: null, face: null, skipDefaults: true));
        Assert.Equal(DecisionOutcome.Rejected, result.Outcome);
        Assert.Contains(ReasonCodes.DocumentFailed, result.ReasonCodes);
        // Skipped downstream checks after a hard fail aren't treated as gaps.
        Assert.DoesNotContain(ReasonCodes.LivenessCheckMissing, result.ReasonCodes);
        Assert.DoesNotContain(ReasonCodes.FaceMatchMissing, result.ReasonCodes);
    }

    [Fact]
    public void Rejects_spoofed_liveness()
    {
        var result = _engine.Decide(Input(live: new CheckSummary(CheckOutcome.Failed, 0.05m, []), face: null)
            with
        { FaceMatch = null });
        Assert.Equal(DecisionOutcome.Rejected, result.Outcome);
        Assert.Contains(ReasonCodes.LivenessFailed, result.ReasonCodes);
    }

    [Theory]
    [InlineData(0.30, DecisionOutcome.Rejected)]     // below reject threshold
    [InlineData(0.72, DecisionOutcome.ManualReview)] // grey zone
    [InlineData(0.90, DecisionOutcome.Approved)]     // above approve threshold
    public void Face_match_score_bands(double score, DecisionOutcome expected)
    {
        var outcome = score < 0.6 ? CheckOutcome.Failed : score < 0.85 ? CheckOutcome.Inconclusive : CheckOutcome.Passed;
        var result = _engine.Decide(Input(face: new CheckSummary(outcome, (decimal)score, [])));
        Assert.Equal(expected, result.Outcome);
    }

    [Fact]
    public void Repeat_attempt_after_rejection_goes_to_manual_review_not_auto_reject()
    {
        var result = _engine.Decide(Input(signals: Clean with { PriorRejectionsForSameDocument = 1 }));
        Assert.Equal(DecisionOutcome.ManualReview, result.Outcome);
        Assert.Contains(ReasonCodes.RepeatAttemptAfterRejection, result.ReasonCodes);
    }

    [Fact]
    public void Expired_document_is_rejected()
    {
        var result = _engine.Decide(Input(signals: Clean with { DocumentExpired = true }));
        Assert.Equal(DecisionOutcome.Rejected, result.Outcome);
        Assert.Contains(ReasonCodes.DocumentExpired, result.ReasonCodes);
    }

    [Fact]
    public void Missing_checks_never_approve()
    {
        var result = _engine.Decide(Input(doc: Pass(), live: null, face: null, skipDefaults: true));
        Assert.Equal(DecisionOutcome.ManualReview, result.Outcome);
        Assert.Contains(ReasonCodes.LivenessCheckMissing, result.ReasonCodes);
    }

    [Fact]
    public void Reject_takes_precedence_and_keeps_review_reasons()
    {
        var result = _engine.Decide(Input(face: new CheckSummary(CheckOutcome.Failed, 0.2m, []),
            signals: Clean with { SelfieReusedInOtherSessions = 2 }));
        Assert.Equal(DecisionOutcome.Rejected, result.Outcome);
        Assert.Contains(ReasonCodes.FaceMismatch, result.ReasonCodes);
        Assert.Contains(ReasonCodes.SelfieReused, result.ReasonCodes);
    }

    [Fact]
    public void Ip_velocity_above_policy_triggers_review()
    {
        var result = _engine.Decide(Input(signals: Clean with { SessionsFromSameIpLast24h = 50 }));
        Assert.Equal(DecisionOutcome.ManualReview, result.Outcome);
        Assert.Contains(ReasonCodes.HighIpVelocity, result.ReasonCodes);
    }

    [Fact]
    public void Risk_score_is_capped_at_100()
    {
        var result = _engine.Decide(new DecisionInput(
            new CheckSummary(CheckOutcome.Failed, 0, []), new CheckSummary(CheckOutcome.Failed, 0, []),
            new CheckSummary(CheckOutcome.Failed, 0, []), new RiskSignals(3, 3, 99, 3, true)));
        Assert.Equal(100, result.RiskScore);
    }
}
