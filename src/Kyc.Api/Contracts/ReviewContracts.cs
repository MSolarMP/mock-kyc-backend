using Kyc.Domain;

namespace Kyc.Api.Contracts;

// Reviewer (back-office) requests and views.

public sealed record ReviewQueueItem(Guid SessionId, string ApplicantReference,
    IReadOnlyList<string> ReasonCodes, int? RiskScore, DateTimeOffset WaitingSince);

public sealed record ReviewCaseView(SessionView Session, ExtractedIdentityView? Identity,
    IReadOnlyList<UploadedFileLink> Images, IReadOnlyList<RelatedSessionView> RelatedSessions);

public sealed record ExtractedIdentityView(string FullName, DateOnly DateOfBirth, string DocumentType,
    string DocumentNumberMasked, DateOnly? ExpiryDate, string IssuingCountry);

public sealed record UploadedFileLink(Guid UploadedFileId, UploadedFileType Type, string Url, DateTimeOffset ExpiresAt);

// Same partner's sessions with the same document (matched by HMAC).
public sealed record RelatedSessionView(Guid SessionId, SessionStatus Status, DateTimeOffset CreatedAt);

public sealed record ReviewDecisionRequest(DecisionOutcome Outcome, string ReasonCode, string? Notes);
