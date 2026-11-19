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

// Timeout and circuit breaker per vendor. No retries here: the job queue retries, which survives a crash.
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

    /// <summary>Turns timeouts and open circuits into <see cref="TransientProviderException"/>.</summary>
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
