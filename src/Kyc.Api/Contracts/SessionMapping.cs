using Kyc.Domain;

namespace Kyc.Api.Contracts;

// Turns a VerificationSession into the next action string the UI should take.
public static class NextActions
{
    public const string UploadDocument = "UPLOAD_DOCUMENT";
    public const string UploadSelfie = "UPLOAD_SELFIE";
    public const string Wait = "WAIT";
    public const string Complete = "COMPLETE";
    public const string Expired = "EXPIRED";

    public static string For(VerificationSession s, DateTimeOffset now) => s.Status switch
    {
        _ when s.HasLapsed(now) => Expired,
        SessionStatus.Created => UploadDocument,
        SessionStatus.AwaitingSelfie => UploadSelfie,
        SessionStatus.Approved or SessionStatus.Rejected => Complete,
        SessionStatus.Expired => Expired,
        _ => Wait, // processing, or waiting for a human reviewer
    };
}

// Maps a session to the partner view and the (reduced) customer view.
public static class Mapping
{
    public static SessionView ToView(this VerificationSession s, DateTimeOffset now) => new(
        s.Id, s.ApplicantReference, s.Channel, s.Status, NextActions.For(s, now),
        s.DocumentAttempts, s.SelfieAttempts, s.CreatedAt, s.UpdatedAt, s.ExpiresAt, s.CompletedAt,
        s.UploadedFiles.OrderBy(a => a.CreatedAt)
            .Select(a => new UploadedFileView(a.Id, a.Type, a.Status, a.ContentType, a.DeclaredSizeBytes,
                a.IntegrityVerified, a.UploadedAt)).ToList(),
        s.Checks.OrderBy(c => c.CompletedAt)
            .Select(c => new CheckView(c.Type, c.Provider, c.ProviderReference, c.Outcome, c.Score, c.ReasonCodes,
                c.Attempt, c.DurationMs, c.CompletedAt)).ToList(),
        s.Decisions.OrderBy(d => d.DecidedAt)
            .Select(d => new DecisionView(d.Outcome, d.DecidedBy, d.ReviewerId, d.ReasonCodes, d.Notes,
                d.RulesVersion, d.RiskScore, d.DecidedAt)).ToList());

    public static ClientSessionView ToClientView(this VerificationSession s, DateTimeOffset now) => new(
        s.HasLapsed(now) ? SessionStatus.Expired : s.Status,
        NextActions.For(s, now),
        RecaptureRequested: (s.Status == SessionStatus.Created && s.DocumentAttempts > 0)
                            || (s.Status == SessionStatus.AwaitingSelfie && s.SelfieAttempts > 0),
        VerificationSession.MaxDocumentAttempts - s.DocumentAttempts,
        VerificationSession.MaxSelfieAttempts - s.SelfieAttempts,
        s.ExpiresAt);
}
