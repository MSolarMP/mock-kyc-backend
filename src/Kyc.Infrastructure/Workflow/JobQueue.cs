using System.Text.Json;
using Kyc.Domain;
using Kyc.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kyc.Infrastructure.Workflow;

public sealed record ArtifactJobPayload(Guid ArtifactId);
public sealed record EmptyPayload;

public static class JobQueue
{
    // Only tracked here. It commits with the state change that caused it (transactional outbox).
    public static WorkflowJob Enqueue(this KycDbContext db, Guid sessionId, JobType type, object payload,
        string? correlationId, DateTimeOffset now)
    {
        var job = WorkflowJob.Create(sessionId, type, JsonSerializer.Serialize(payload, KycDbContext.Json),
            correlationId, now);
        db.WorkflowJobs.Add(job);
        return job;
    }

    public static T ReadPayload<T>(this WorkflowJob job) =>
        JsonSerializer.Deserialize<T>(job.Payload, KycDbContext.Json)
        ?? throw new InvalidOperationException($"Job {job.Id} has an empty payload.");

    // SKIP LOCKED so workers never double-claim. If a worker dies, the lease expires and the job is retried,
    // so handlers must be idempotent. Attempts go up on claim so a job that crashes workers still dead-letters.
    public static Task<List<WorkflowJob>> ClaimAsync(this KycDbContext db, string workerId, int batchSize,
        TimeSpan lease, CancellationToken ct) =>
        db.WorkflowJobs.FromSqlInterpolated($"""
            UPDATE workflow_jobs
               SET status = 'Running',
                   locked_by = {workerId},
                   locked_until = now() + {lease},
                   attempts = attempts + 1
             WHERE id IN (
                   SELECT id FROM workflow_jobs
                    WHERE (status = 'Pending' AND run_after <= now())
                       OR (status = 'Running' AND locked_until < now())
                    ORDER BY run_after
                    LIMIT {batchSize}
                      FOR UPDATE SKIP LOCKED)
            RETURNING *
            """).AsNoTracking().ToListAsync(ct);
}
