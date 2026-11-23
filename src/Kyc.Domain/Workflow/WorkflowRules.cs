namespace Kyc.Domain.Workflow;

public enum NextStep
{
    Continue,
    RequestRecapture,
    // Skip remaining checks.
    GoToDecision,
}

// Only image quality problems get a retry. Anything definitive goes straight to the decision.
public static class WorkflowRules
{
    // What to do after a document check: continue, ask for a recapture, or go straight to the decision.
    public static NextStep AfterDocumentCheck(CheckOutcome outcome, int attemptsUsed) => outcome switch
    {
        CheckOutcome.Passed or CheckOutcome.Inconclusive => NextStep.Continue,
        CheckOutcome.Unreadable when attemptsUsed < VerificationSession.MaxDocumentAttempts => NextStep.RequestRecapture,
        _ => NextStep.GoToDecision,
    };

    // What to do after a liveness check: continue to face match, ask for a recapture, or go to the decision.
    public static NextStep AfterLivenessCheck(CheckOutcome outcome, int attemptsUsed) => outcome switch
    {
        CheckOutcome.Passed or CheckOutcome.Inconclusive => NextStep.Continue,
        CheckOutcome.Unreadable when attemptsUsed < VerificationSession.MaxSelfieAttempts => NextStep.RequestRecapture,
        _ => NextStep.GoToDecision,
    };

    // Exponential backoff with jitter, capped at 5 min.
    public static TimeSpan RetryDelay(int attempt, Random? random = null)
    {
        var baseSeconds = Math.Min(Math.Pow(2, attempt), 300);
        var jitter = (random ?? Random.Shared).NextDouble() * baseSeconds * 0.2;
        return TimeSpan.FromSeconds(baseSeconds + jitter);
    }
}
