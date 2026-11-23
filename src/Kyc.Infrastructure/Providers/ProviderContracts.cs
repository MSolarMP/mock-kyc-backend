using Kyc.Domain;

namespace Kyc.Infrastructure.Providers;

// Vendor-neutral contracts. Each vendor has an adapter that maps onto these.
public sealed record ProviderRequest(
    Guid SessionId,
    Guid UploadedFileId,
    // Short-lived pre-signed URL the vendor downloads the image from.
    Uri ImageUrl,
    string StorageKey,
    // Same on every retry of a job.
    string IdempotencyKey,
    int Attempt);

public sealed record FaceMatchRequest(
    Guid SessionId,
    Guid SelfieUploadedFileId,
    Uri SelfieUrl,
    string SelfieStorageKey,
    Uri DocumentUrl,
    string DocumentStorageKey,
    string IdempotencyKey,
    int Attempt);

public record ProviderResult(
    CheckOutcome Outcome,
    decimal? Score,
    IReadOnlyList<string> ReasonCodes,
    string ProviderReference);

public sealed record ExtractedIdentity(
    string DocumentNumber,
    string DocumentType,
    string FullName,
    DateOnly DateOfBirth,
    DateOnly? ExpiryDate,
    string IssuingCountry);

public sealed record DocumentResult(
    CheckOutcome Outcome,
    decimal? Score,
    IReadOnlyList<string> ReasonCodes,
    string ProviderReference,
    ExtractedIdentity? Identity)
    : ProviderResult(Outcome, Score, ReasonCodes, ProviderReference);

public interface IDocumentVerificationProvider
{
    string Name { get; }
    Task<DocumentResult> VerifyAsync(ProviderRequest request, CancellationToken ct);
}

public interface ILivenessProvider
{
    string Name { get; }
    Task<ProviderResult> CheckAsync(ProviderRequest request, CancellationToken ct);
}

public interface IFaceMatchProvider
{
    string Name { get; }
    Task<ProviderResult> MatchAsync(FaceMatchRequest request, CancellationToken ct);
}

// Retryable failure (timeout, 5xx, rate limit, circuit open). The job retries with backoff.
public sealed class TransientProviderException(string message, Exception? inner = null) : Exception(message, inner);

// Failure a retry can't fix (e.g. 401). The job is dead-lettered and the session goes to manual review.
public sealed class PermanentProviderException(string message, Exception? inner = null) : Exception(message, inner);
