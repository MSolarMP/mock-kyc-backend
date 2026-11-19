namespace Kyc.Domain;

// The API maps Code to the problem+json error code.
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

// 409
public sealed class SessionStateConflictException(string code, string message) : DomainException(code, message);

// 410
public sealed class SessionExpiredException()
    : DomainException("session_expired", "The verification session has expired. Start a new session.");

public sealed class NotFoundException(string resource)
    : DomainException("not_found", $"{resource} was not found.");

// 422
public sealed class BusinessRuleException(string code, string message) : DomainException(code, message);
