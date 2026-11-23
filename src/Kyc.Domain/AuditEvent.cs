namespace Kyc.Domain;

// Append-only. A DB trigger blocks UPDATE and DELETE.
public class AuditEvent
{
    public long Id { get; set; }
    public Guid SessionId { get; set; }
    public string EventType { get; set; } = default!;
    public ActorType ActorType { get; set; }
    public string? ActorId { get; set; }
    public string? CorrelationId { get; set; }
    public string? IpAddress { get; set; }
    // JSON: ids, codes and hashes only.
    public string Data { get; set; } = "{}";
    public DateTimeOffset OccurredAt { get; set; }
}
