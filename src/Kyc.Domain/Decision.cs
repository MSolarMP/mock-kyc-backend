namespace Kyc.Domain;

// Can have several per session: system says ManualReview, then a reviewer decides.
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
    // Rules version the automated decision used.
    public string? RulesVersion { get; private set; }
    public int? RiskScore { get; private set; }
    public DateTimeOffset DecidedAt { get; private set; }

    // A decision made by the rules engine; carries the rules version and risk score.
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

    // A reviewer's approve/reject decision; ManualReview isn't a valid reviewer outcome.
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
