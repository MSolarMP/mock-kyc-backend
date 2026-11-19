namespace Kyc.Domain;

/// <summary>
/// The single source of truth for which status changes are legal. Every transition goes through
/// <see cref="VerificationSession.TransitionTo"/>, which checks this table and writes an audit event.
/// </summary>
/// <remarks>
/// <code>
///   Created ──upload doc──▶ DocumentSubmitted ──doc ok──▶ AwaitingSelfie ──upload selfie──▶ SelfieSubmitted
///      ▲                         │  │                          ▲                              │  │
///      └──── doc unreadable ─────┘  │                          └────── selfie unreadable ─────┘  │
///                                   │ doc failed / retries used up / provider down               │
///                                   ▼                                                            ▼
///                             PendingDecision ◀───────────────── liveness + face match done ─────┘
///                                   │
///                 ┌─────────────────┼───────────────────┐
///                 ▼                 ▼                   ▼
///             Approved          Rejected          ManualReview ──reviewer──▶ Approved | Rejected
///
///   Created / AwaitingSelfie ──timeout──▶ Expired
/// </code>
/// Only states that wait on the customer can expire. A session the platform is still processing
/// never expires, so a slow vendor can't make a customer lose their progress.
/// </remarks>
public static class SessionStateMachine
{
    private static readonly Dictionary<SessionStatus, SessionStatus[]> Allowed = new()
    {
        [SessionStatus.Created] = [SessionStatus.DocumentSubmitted, SessionStatus.Expired],
        [SessionStatus.DocumentSubmitted] =
        [
            SessionStatus.AwaitingSelfie,
            SessionStatus.Created,          // recapture requested
            SessionStatus.PendingDecision,  // hard fail or retries used up
            SessionStatus.ManualReview,     // provider unavailable after all retries
        ],
        [SessionStatus.AwaitingSelfie] = [SessionStatus.SelfieSubmitted, SessionStatus.Expired],
        [SessionStatus.SelfieSubmitted] =
        [
            SessionStatus.AwaitingSelfie,   // recapture requested
            SessionStatus.PendingDecision,
            SessionStatus.ManualReview,
        ],
        [SessionStatus.PendingDecision] =
            [SessionStatus.Approved, SessionStatus.Rejected, SessionStatus.ManualReview],
        [SessionStatus.ManualReview] = [SessionStatus.Approved, SessionStatus.Rejected],
        [SessionStatus.Approved] = [],
        [SessionStatus.Rejected] = [],
        [SessionStatus.Expired] = [],
    };

    public static bool CanTransition(SessionStatus from, SessionStatus to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public static bool IsTerminal(SessionStatus status) =>
        status is SessionStatus.Approved or SessionStatus.Rejected or SessionStatus.Expired;

    /// <summary>States in which the platform is waiting on the customer, so the session may expire.</summary>
    public static bool IsAwaitingCustomer(SessionStatus status) =>
        status is SessionStatus.Created or SessionStatus.AwaitingSelfie;

    public static IReadOnlyCollection<SessionStatus> AllowedTargets(SessionStatus from) => Allowed[from];
}
