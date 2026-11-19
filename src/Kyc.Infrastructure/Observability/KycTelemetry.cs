using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Kyc.Infrastructure.Observability;

/// <summary>
/// Business and workflow metrics. These answer the operational questions: is throughput keeping up (job lag),
/// is a vendor degrading (call outcomes/latency), and is the approval rate drifting (decisions by outcome).
/// </summary>
public sealed class KycMetrics
{
    public const string MeterName = "Kyc";
    public static readonly ActivitySource ActivitySource = new("Kyc.Workflow");

    private readonly Counter<long> _sessionsCreated;
    private readonly Counter<long> _artifactsUploaded;
    private readonly Counter<long> _providerCalls;
    private readonly Histogram<double> _providerLatency;
    private readonly Counter<long> _decisions;
    private readonly Counter<long> _jobsProcessed;
    private readonly Histogram<double> _jobQueueLag;
    private readonly Counter<long> _handoffs;

    public KycMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _sessionsCreated = meter.CreateCounter<long>("kyc.sessions.created");
        _artifactsUploaded = meter.CreateCounter<long>("kyc.artifacts.uploaded");
        _providerCalls = meter.CreateCounter<long>("kyc.provider.calls");
        _providerLatency = meter.CreateHistogram<double>("kyc.provider.latency", unit: "ms");
        _decisions = meter.CreateCounter<long>("kyc.decisions");
        _jobsProcessed = meter.CreateCounter<long>("kyc.jobs.processed");
        _jobQueueLag = meter.CreateHistogram<double>("kyc.jobs.queue_lag", unit: "ms",
            description: "Time between a job becoming runnable and a worker claiming it. The main autoscaling signal for workers.");
        _handoffs = meter.CreateCounter<long>("kyc.handoffs");
    }

    public void SessionCreated(string channel) => _sessionsCreated.Add(1, T("channel", channel));
    public void ArtifactUploaded(string type) => _artifactsUploaded.Add(1, T("type", type));

    public void ProviderCall(string check, string provider, string outcome, double ms)
    {
        _providerCalls.Add(1, T("check", check), T("provider", provider), T("outcome", outcome));
        _providerLatency.Record(ms, T("check", check), T("provider", provider));
    }

    public void Decision(string outcome, string decidedBy) =>
        _decisions.Add(1, T("outcome", outcome), T("decided_by", decidedBy));

    public void JobProcessed(string type, string result) =>
        _jobsProcessed.Add(1, T("type", type), T("result", result));

    public void JobLag(string type, double ms) => _jobQueueLag.Record(ms, T("type", type));
    public void Handoff(string result) => _handoffs.Add(1, T("result", result));

    private static KeyValuePair<string, object?> T(string key, object? value) => new(key, value);
}
