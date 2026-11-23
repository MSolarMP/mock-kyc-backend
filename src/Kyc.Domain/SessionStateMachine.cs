namespace Kyc.Domain;

// Which status changes are allowed. VerificationSession.TransitionTo checks this table.
//
//   Created ──upload doc──▶ DocumentSubmitted ──doc ok──▶ AwaitingSelfie ──upload selfie──▶ SelfieSubmitted
//      ▲                         │  │                          ▲                              │  │
//      └──── doc unreadable ─────┘  │                          └────── selfie unreadable ─────┘  │
//                                   │ doc failed / retries used up / provider down               │
//                                   ▼                                                            ▼
//                             PendingDecision ◀───────────────── liveness + face match done ─────┘
//                                   │
//                 ┌─────────────────┼───────────────────┐
//                 ▼                 ▼                   ▼
//             Approved          Rejected          ManualReview ──reviewer──▶ Approved | Rejected
//
//   Created / AwaitingSelfie ──timeout──▶ Expired
//
// Only states waiting on the customer can expire.
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

    public static bool IsAwaitingCustomer(SessionStatus status) =>
        status is SessionStatus.Created or SessionStatus.AwaitingSelfie;

    public static IReadOnlyCollection<SessionStatus> AllowedTargets(SessionStatus from) => Allowed[from];
}
