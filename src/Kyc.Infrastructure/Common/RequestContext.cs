using Kyc.Domain;

namespace Kyc.Infrastructure.Common;

// Current actor and correlation id. Set by API middleware or the job processor.
public interface IRequestContext
{
    ActorType ActorType { get; }
    string? ActorId { get; }
    string? CorrelationId { get; }
    string? IpAddress { get; }
    string? UserAgent { get; }
}

public sealed class RequestContext : IRequestContext
{
    public ActorType ActorType { get; private set; } = ActorType.System;
    public string? ActorId { get; private set; } = "system";
    public string? CorrelationId { get; private set; }
    public string? IpAddress { get; private set; }
    public string? UserAgent { get; private set; }

    public void SetActor(ActorType type, string? id)
    {
        ActorType = type;
        ActorId = id;
    }

    public void SetRequest(string? correlationId, string? ip, string? userAgent)
    {
        CorrelationId = correlationId;
        IpAddress = ip;
        UserAgent = userAgent;
    }
}
