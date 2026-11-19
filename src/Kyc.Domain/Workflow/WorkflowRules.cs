namespace Kyc.Domain.Workflow;

/// <summary>What the workflow should do after a provider check finishes.</summary>
public enum NextStep
{
    /// <summary>Move on to the next step in the happy path.</summary>
    Continue,
    /// <summary>Ask the customer to capture the image again.</summary>
    RequestRecapture,
    /// <summary>Skip the remaining checks and go straight to the decision.</summary>
    GoToDecision,
}

/// <summary>
/// Routing rules per step, kept apart from the job handlers so they're easy to read and test.
/// The guiding rule: only what the customer can fix (image quality) earns a retry. Anything definitive goes to
/// the decision engine at once, so we don't spend vendor fees or customer time on a session that will fail anyway.
/// </summary>
public static class WorkflowRules
{
    public static NextStep AfterDocumentCheck(CheckOutcome outcome, int attemptsUsed) => outcome switch
    {
        CheckOutcome.Passed or CheckOutcome.Inconclusive => NextStep.Continue,
        CheckOutcome.Unreadable when attemptsUsed < VerificationSession.MaxDocumentAttempts => NextStep.RequestRecapture,
        _ => NextStep.GoToDecision,
    };

    public static NextStep AfterLivenessCheck(CheckOutcome outcome, int attemptsUsed) => outcome switch
    {
        CheckOutcome.Passed or CheckOutcome.Inconclusive => NextStep.Continue,
        CheckOutcome.Unreadable when attemptsUsed < VerificationSession.MaxSelfieAttempts => NextStep.RequestRecapture,
        _ => NextStep.GoToDecision,
    };

    /// <summary>Exponential backoff with jitter for durable job retries (attempt 1 → ~2s, 2 → ~4s, ... capped at 5 min).</summary>
    public static TimeSpan RetryDelay(int attempt, Random? random = null)
    {
        var baseSeconds = Math.Min(Math.Pow(2, attempt), 300);
        var jitter = (random ?? Random.Shared).NextDouble() * baseSeconds * 0.2;
        return TimeSpan.FromSeconds(baseSeconds + jitter);
    }
}
