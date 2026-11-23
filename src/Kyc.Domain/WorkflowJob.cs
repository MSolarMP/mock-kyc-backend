namespace Kyc.Domain;

// Outbox job. Saved in the same transaction as the state change that created it.
public class WorkflowJob
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public JobType Type { get; set; }
    public string Payload { get; set; } = "{}";
    public JobStatus Status { get; set; }
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; }
    public DateTimeOffset RunAfter { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public string? LockedBy { get; set; }
    public string? LastError { get; set; }
    public string? CorrelationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public static WorkflowJob Create(Guid sessionId, JobType type, string payload, string? correlationId,
        DateTimeOffset now, int maxAttempts = 5) => new()
        {
            Id = Guid.CreateVersion7(now),
            SessionId = sessionId,
            Type = type,
            Payload = payload,
            Status = JobStatus.Pending,
            MaxAttempts = maxAttempts,
            RunAfter = now,
            CorrelationId = correlationId,
            CreatedAt = now,
        };
}
