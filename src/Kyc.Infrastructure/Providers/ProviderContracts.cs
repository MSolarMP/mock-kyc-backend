using Kyc.Domain;

namespace Kyc.Infrastructure.Providers;

/// <summary>
/// Vendor-neutral contracts. Each real vendor gets an adapter that maps its API and score scales onto these types,
/// so the workflow, data model and decision rules never mention a vendor. Changing or adding a vendor is
/// a new adapter plus a config change.
/// </summary>
public sealed record ProviderRequest(
    Guid SessionId,
    Guid ArtifactId,
    /// <summary>Short-lived pre-signed GET URL. Real vendors fetch the image themselves, so the bytes don't pass through us.</summary>
    Uri ImageUrl,
    string StorageKey,
    /// <summary>Stable per job, so a retried call can be de-duplicated by vendors that support idempotency keys.</summary>
    string IdempotencyKey,
    int Attempt);

public sealed record FaceMatchRequest(
    Guid SessionId,
    Guid SelfieArtifactId,
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

/// <summary>
/// A retryable failure (timeout, 5xx, rate limit, circuit open). The job processor reschedules with backoff.
/// Anything else that a provider throws is also retried, but this type makes the intent explicit.
/// </summary>
public sealed class TransientProviderException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// A failure retrying can't fix (e.g. 400/401 from the vendor: our request or credentials are wrong). The job processor
/// dead-letters the job immediately and the session goes to manual review; operators are alerted to fix the cause.
/// </summary>
public sealed class PermanentProviderException(string message, Exception? inner = null) : Exception(message, inner);
