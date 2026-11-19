using Kyc.Domain;

namespace Kyc.Infrastructure.Common;

/// <summary>
/// Who is acting, and under which correlation id. Filled in by API middleware for HTTP requests, and by the job
/// processor for background work. <see cref="Persistence.KycDbContext"/> stamps it on every audit event.
/// </summary>
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
