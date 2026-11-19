using System.Diagnostics;
using Kyc.Domain;
using Kyc.Domain.Workflow;
using Kyc.Infrastructure.Common;
using Kyc.Infrastructure.Observability;
using Kyc.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kyc.Infrastructure.Workflow;

public sealed class WorkerOptions
{
    public const string Section = "Worker";
    public int PollIntervalMs { get; set; } = 1000;
    public int BatchSize { get; set; } = 10;
    public int MaxConcurrency { get; set; } = 4;
    public int LeaseSeconds { get; set; } = 60;
}

public interface IJobHandler
{
    JobType Type { get; }

    /// <summary>
    /// Do the work by changing tracked entities on <paramref name="db"/>. Don't call SaveChanges: the processor saves
    /// the handler's changes and the job's completion together, in one transaction.
    /// </summary>
    Task HandleAsync(WorkflowJob job, KycDbContext db, CancellationToken ct);

    /// <summary>Called once when retries run out, before the job is dead-lettered. Must leave the session in a state that can make progress.</summary>
    Task OnExhaustedAsync(WorkflowJob job, KycDbContext db, Exception error, CancellationToken ct);
}

/// <summary>
/// Polls the Postgres job queue and runs handlers. Scale out by running more worker replicas: SKIP LOCKED
/// spreads jobs between them with no coordinator.
/// </summary>
public sealed class JobProcessor(
    IServiceScopeFactory scopes,
    IOptions<WorkerOptions> options,
    TimeProvider clock,
    KycMetrics metrics,
    ILogger<JobProcessor> logger) : BackgroundService
{
    // Unique per process; stored in locked_by so an operator can see which instance holds a job.
    private readonly string _workerId = Truncate($"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}", 128);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        logger.LogInformation("Job processor {WorkerId} started (batch {Batch}, concurrency {Concurrency})",
            _workerId, o.BatchSize, o.MaxConcurrency);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                List<WorkflowJob> claimed;
                await using (var scope = scopes.CreateAsyncScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<KycDbContext>();
                    claimed = await db.ClaimAsync(_workerId, o.BatchSize, TimeSpan.FromSeconds(o.LeaseSeconds), stoppingToken);
                }

                if (claimed.Count == 0)
                {
                    await Task.Delay(o.PollIntervalMs, stoppingToken);
                    continue;
                }

                var now = clock.GetUtcNow();
                foreach (var j in claimed) metrics.JobLag(j.Type.ToString(), (now - j.RunAfter).TotalMilliseconds);

                await Parallel.ForEachAsync(claimed,
                    new ParallelOptions { MaxDegreeOfParallelism = o.MaxConcurrency, CancellationToken = stoppingToken },
                    async (job, ct) => await ProcessAsync(job, ct));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Database blip etc. Back off and keep the loop alive.
                logger.LogError(ex, "Job polling loop failed; backing off");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    internal async Task ProcessAsync(WorkflowJob claimed, CancellationToken ct)
    {
        using var activity = KycMetrics.ActivitySource.StartActivity($"job {claimed.Type}", ActivityKind.Consumer);
        activity?.SetTag("kyc.job_id", claimed.Id);
        activity?.SetTag("kyc.session_id", claimed.SessionId);
        activity?.SetTag("kyc.correlation_id", claimed.CorrelationId);
        using var logScope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = claimed.CorrelationId,
            ["SessionId"] = claimed.SessionId,
            ["JobId"] = claimed.Id,
            ["JobType"] = claimed.Type.ToString(),
            ["Attempt"] = claimed.Attempts,
        });

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var (db, handler) = Resolve(scope.ServiceProvider, claimed);

            var job = await db.WorkflowJobs.SingleAsync(j => j.Id == claimed.Id, ct);
            if (job.LockedBy != _workerId || job.Status != JobStatus.Running)
            {
                logger.LogWarning("Lost lease on job before processing; skipping");
                return;
            }

            await handler.HandleAsync(job, db, ct);

            job.Status = JobStatus.Completed;
            job.CompletedAt = clock.GetUtcNow();
            job.LockedUntil = null;
            job.LastError = null;
            await db.SaveChangesAsync(ct);

            metrics.JobProcessed(claimed.Type.ToString(), "completed");
            logger.LogInformation("Job completed");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down: the lease runs out and another worker picks the job up.
        }
        catch (Exception ex)
        {
            await HandleFailureAsync(claimed, ex, ct);
        }
    }

    private async Task HandleFailureAsync(WorkflowJob claimed, Exception error, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var message = Truncate($"{error.GetType().Name}: {error.Message}", 1000);

        // Optimistic-concurrency conflicts mean another writer changed the session under us; retry soon without much backoff.
        var concurrency = error is DbUpdateConcurrencyException;

        await using var scope = scopes.CreateAsyncScope();
        var (db, handler) = Resolve(scope.ServiceProvider, claimed);

        if (claimed.Attempts >= claimed.MaxAttempts)
        {
            logger.LogError(error, "Job exhausted {Attempts} attempts; dead-lettering", claimed.Attempts);
            try
            {
                await handler.OnExhaustedAsync(claimed, db, error, ct);
                await db.SaveChangesAsync(ct);
            }
            catch (Exception compensationError)
            {
                logger.LogError(compensationError, "Exhaustion handler failed; job needs operator attention");
            }

            await db.WorkflowJobs.Where(j => j.Id == claimed.Id).ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, JobStatus.DeadLettered)
                .SetProperty(j => j.LastError, message)
                .SetProperty(j => j.LockedUntil, (DateTimeOffset?)null)
                .SetProperty(j => j.CompletedAt, now), ct);
            metrics.JobProcessed(claimed.Type.ToString(), "dead_lettered");
            return;
        }

        var delay = concurrency ? TimeSpan.FromMilliseconds(200) : WorkflowRules.RetryDelay(claimed.Attempts);
        logger.LogWarning(error, "Job failed (attempt {Attempt}/{Max}); retrying in {Delay}",
            claimed.Attempts, claimed.MaxAttempts, delay);

        await db.WorkflowJobs.Where(j => j.Id == claimed.Id && j.LockedBy == _workerId).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Status, JobStatus.Pending)
            .SetProperty(j => j.RunAfter, now.Add(delay))
            .SetProperty(j => j.LastError, message)
            .SetProperty(j => j.LockedBy, (string?)null)
            .SetProperty(j => j.LockedUntil, (DateTimeOffset?)null), ct);
        metrics.JobProcessed(claimed.Type.ToString(), "retry_scheduled");
    }

    private static (KycDbContext Db, IJobHandler Handler) Resolve(IServiceProvider sp, WorkflowJob job)
    {
        var context = (RequestContext)sp.GetRequiredService<IRequestContext>();
        context.SetActor(ActorType.System, $"worker:{job.Type}");
        context.SetRequest(job.CorrelationId, null, null);

        var handler = sp.GetServices<IJobHandler>().SingleOrDefault(h => h.Type == job.Type)
                      ?? throw new InvalidOperationException($"No handler registered for {job.Type}.");
        return (sp.GetRequiredService<KycDbContext>(), handler);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
