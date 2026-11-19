using System.Text.Json;
using Kyc.Domain;
using Kyc.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kyc.Infrastructure.Workflow;

public sealed record ArtifactJobPayload(Guid ArtifactId);
public sealed record EmptyPayload;

public static class JobQueue
{
    /// <summary>
    /// Adds a job to the change tracker. It commits with whatever <c>SaveChanges</c> persists the state change
    /// that caused it (this table is our transactional outbox).
    /// </summary>
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

    /// <summary>
    /// Atomically claims up to <paramref name="batchSize"/> runnable jobs for this worker.
    /// <list type="bullet">
    /// <item><c>FOR UPDATE SKIP LOCKED</c>: competing workers never block on, or double-claim, the same row.</item>
    /// <item>Lease (<c>locked_until</c>): if a worker dies mid-job, the lease runs out and another worker picks it up.
    /// This gives at-least-once delivery, so handlers are idempotent.</item>
    /// <item><c>attempts</c> goes up on claim, so a job that keeps crashing its worker still reaches the dead-letter state.</item>
    /// </list>
    /// </summary>
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
