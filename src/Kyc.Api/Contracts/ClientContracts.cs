using Kyc.Domain;

namespace Kyc.Api.Contracts;

// Customer device requests and views.

public sealed record ClientSessionView(
    SessionStatus Status,
    string NextAction,
    bool RecaptureRequested,
    int DocumentAttemptsRemaining,
    int SelfieAttemptsRemaining,
    DateTimeOffset ExpiresAt);

public sealed record CreateHandoffResponse(string HandoffUrl, DateTimeOffset ExpiresAt);

public sealed record RedeemHandoffRequest(string Token, string? DeviceFingerprint);

public sealed record RedeemHandoffResponse(string ClientToken, DateTimeOffset ClientTokenExpiresAt, ClientSessionView Session);

public sealed record CreateUploadRequest(UploadedFileType Type, string ContentType, long SizeBytes, string Sha256);

public sealed record CreateUploadResponse(
    Guid UploadedFileId,
    string UploadUrl,
    string UploadMethod,
    IReadOnlyDictionary<string, string> UploadHeaders,
    DateTimeOffset ExpiresAt);
