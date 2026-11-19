using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Kyc.Infrastructure.Providers;

public sealed class ProviderResilienceOptions
{
    public const string Section = "Providers:Resilience";
    public int TimeoutSeconds { get; set; } = 15;
    public double CircuitFailureRatio { get; set; } = 0.5;
    public int CircuitMinimumThroughput { get; set; } = 10;
    public int CircuitBreakSeconds { get; set; } = 30;
}

/// <summary>
/// In-process resilience around each vendor: a timeout so a slow vendor can't hold a worker, and a circuit breaker so
/// we fail fast (and back off) while a vendor is degraded, rather than piling up requests.
/// Retries are deliberately <b>not</b> done here: they happen at the job level (<see cref="Workflow.JobProcessor"/>) with
/// durable backoff, so a retry survives a worker crash and doesn't hold a thread.
/// </summary>
public static class ProviderResilience
{
    public static ResiliencePipeline Build(string provider, ProviderResilienceOptions o) =>
        new ResiliencePipelineBuilder { Name = provider }
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = o.CircuitFailureRatio,
                MinimumThroughput = o.CircuitMinimumThroughput,
                SamplingDuration = TimeSpan.FromSeconds(60),
                BreakDuration = TimeSpan.FromSeconds(o.CircuitBreakSeconds),
                ShouldHandle = new PredicateBuilder().Handle<Exception>(ex => ex is not OperationCanceledException),
            })
            .AddTimeout(TimeSpan.FromSeconds(o.TimeoutSeconds))
            .Build();

    /// <summary>Normalises resilience failures into <see cref="TransientProviderException"/> for the job processor.</summary>
    public static async Task<T> ExecuteAsync<T>(this ResiliencePipeline pipeline, string provider,
        Func<CancellationToken, Task<T>> call, CancellationToken ct)
    {
        try
        {
            return await pipeline.ExecuteAsync(async token => await call(token), ct);
        }
        catch (BrokenCircuitException ex)
        {
            throw new TransientProviderException($"Circuit open for {provider}.", ex);
        }
        catch (TimeoutRejectedException ex)
        {
            throw new TransientProviderException($"{provider} timed out.", ex);
        }
    }
}

/// <summary>Decorators that add the resilience pipeline around any adapter (mock or real).</summary>
public sealed class ResilientDocumentProvider(IDocumentVerificationProvider inner, ResiliencePipeline pipeline)
    : IDocumentVerificationProvider
{
    public string Name => inner.Name;
    public Task<DocumentResult> VerifyAsync(ProviderRequest r, CancellationToken ct) =>
        pipeline.ExecuteAsync(Name, t => inner.VerifyAsync(r, t), ct);
}

public sealed class ResilientLivenessProvider(ILivenessProvider inner, ResiliencePipeline pipeline) : ILivenessProvider
{
    public string Name => inner.Name;
    public Task<ProviderResult> CheckAsync(ProviderRequest r, CancellationToken ct) =>
        pipeline.ExecuteAsync(Name, t => inner.CheckAsync(r, t), ct);
}

public sealed class ResilientFaceMatchProvider(IFaceMatchProvider inner, ResiliencePipeline pipeline) : IFaceMatchProvider
{
    public string Name => inner.Name;
    public Task<ProviderResult> MatchAsync(FaceMatchRequest r, CancellationToken ct) =>
        pipeline.ExecuteAsync(Name, t => inner.MatchAsync(r, t), ct);
}
