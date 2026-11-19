using Kyc.Domain;
using Kyc.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kyc.Infrastructure.Outbox;

/// <summary>
/// Where integration events go: a message bus (SNS/Kafka) or signed webhooks to the onboarding system. Consumers
/// must be idempotent: delivery is at-least-once, and the outbox message id is the dedupe key.
/// </summary>
public interface IEventPublisher
{
    Task PublishAsync(OutboxMessage message, CancellationToken ct);
}

/// <summary>Stand-in publisher: writes events to the structured log. Swap in an SNS/Kafka/webhook implementation for production.</summary>
public sealed class LoggingEventPublisher(ILogger<LoggingEventPublisher> logger) : IEventPublisher
{
    public Task PublishAsync(OutboxMessage message, CancellationToken ct)
    {
        logger.LogInformation("Published integration event {EventType} #{MessageId} for session {SessionId}: {Payload}",
            message.EventType, message.Id, message.SessionId, message.Payload);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Relays committed outbox rows to the publisher. Because the rows are written in the same transaction as the state
/// change, downstream systems never hear about a decision that rolled back, and never miss one that committed.
/// </summary>
public sealed class OutboxPublisher(IServiceScopeFactory scopes, TimeProvider clock, ILogger<OutboxPublisher> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var published = await PublishBatchAsync(stoppingToken);
                if (published == 0) await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox relay failed; backing off");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    internal async Task<int> PublishBatchAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KycDbContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Row locks + SKIP LOCKED let several relay instances run without publishing the same message twice
        // (in the normal case). Ordered by id, which preserves per-session event order.
        var batch = await db.OutboxMessages.FromSqlRaw("""
            SELECT * FROM outbox_messages
             WHERE published_at IS NULL AND attempts < 20
             ORDER BY id
             LIMIT 50
               FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct);

        foreach (var message in batch)
        {
            try
            {
                await publisher.PublishAsync(message, ct);
                message.PublishedAt = clock.GetUtcNow();
            }
            catch (Exception ex)
            {
                message.Attempts++;
                message.LastError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                logger.LogWarning(ex, "Failed to publish outbox message {MessageId}", message.Id);
                break; // Keep ordering: stop at the first failure and retry on the next cycle.
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return batch.Count(m => m.PublishedAt is not null);
    }
}
