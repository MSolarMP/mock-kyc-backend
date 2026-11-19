namespace Kyc.Domain;

/// <summary>Base type for rule violations. The API maps <see cref="Code"/> to a problem+json error code.</summary>
public abstract class DomainException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class InvalidStateTransitionException(SessionStatus from, SessionStatus to)
    : DomainException("invalid_state_transition", $"Cannot move a session from {from} to {to}.")
{
    public SessionStatus From { get; } = from;
    public SessionStatus To { get; } = to;
}

/// <summary>The request is valid, but the session is not in a state that allows it (HTTP 409).</summary>
public sealed class SessionStateConflictException(string code, string message) : DomainException(code, message);

/// <summary>The session can no longer be used by the customer (HTTP 410).</summary>
public sealed class SessionExpiredException()
    : DomainException("session_expired", "The verification session has expired. Start a new session.");

public sealed class NotFoundException(string resource)
    : DomainException("not_found", $"{resource} was not found.");

/// <summary>Input failed a business rule (HTTP 422).</summary>
public sealed class BusinessRuleException(string code, string message) : DomainException(code, message);
