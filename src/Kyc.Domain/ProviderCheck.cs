namespace Kyc.Domain;

// Result of one vendor check against an uploaded file.
public class ProviderCheck
{
    private ProviderCheck() { }

    public Guid Id { get; private set; }
    public Guid SessionId { get; private set; }
    public Guid? UploadedFileId { get; private set; }
    public CheckType Type { get; private set; }
    // e.g. "vendor-a".
    public string Provider { get; private set; } = default!;
    public string? ProviderReference { get; private set; }
    public CheckOutcome Outcome { get; private set; }
    // Normalised to 0..1.
    public decimal? Score { get; private set; }
    public List<string> ReasonCodes { get; private set; } = [];
    public int Attempt { get; private set; }
    public long DurationMs { get; private set; }
    public DateTimeOffset CompletedAt { get; private set; }

    public static ProviderCheck Create(Guid sessionId, Guid? uploadedFileId, CheckType type, string provider,
        string? providerReference, CheckOutcome outcome, decimal? score, IEnumerable<string> reasonCodes,
        int attempt, long durationMs, DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(now),
            SessionId = sessionId,
            UploadedFileId = uploadedFileId,
            Type = type,
            Provider = provider,
            ProviderReference = providerReference,
            Outcome = outcome,
            Score = score,
            ReasonCodes = reasonCodes.ToList(),
            Attempt = attempt,
            DurationMs = durationMs,
            CompletedAt = now,
        };
}
