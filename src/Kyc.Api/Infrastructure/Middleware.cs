using System.Diagnostics;
using System.Text.RegularExpressions;
using Kyc.Domain;
using Kyc.Infrastructure.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Kyc.Api.Infrastructure;

/// <summary>Uses the inbound X-Correlation-Id (or trace id) and carries it into logs, audit events and jobs.</summary>
public sealed partial class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string Header = "X-Correlation-Id";

    [GeneratedRegex("^[A-Za-z0-9._-]{8,64}$")]
    private static partial Regex Valid();

    public async Task InvokeAsync(HttpContext context, RequestContext requestContext)
    {
        var inbound = context.Request.Headers[Header].ToString();
        var correlationId = Valid().IsMatch(inbound)
            ? inbound
            : Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");

        requestContext.SetRequest(correlationId, context.Connection.RemoteIpAddress?.ToString(),
            Truncate(context.Request.Headers.UserAgent.ToString(), 512));
        Activity.Current?.SetTag("kyc.correlation_id", correlationId);
        context.Response.Headers[Header] = correlationId;

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
            await next(context);
    }

    private static string? Truncate(string s, int max) => string.IsNullOrEmpty(s) ? null : s.Length <= max ? s : s[..max];
}

/// <summary>Maps exceptions to problem+json with a stable <c>code</c>. Internal details never reach the client.</summary>
public sealed class ProblemExceptionHandler(IProblemDetailsService problems, ILogger<ProblemExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        var (status, code, detail) = exception switch
        {
            NotFoundException e => (StatusCodes.Status404NotFound, e.Code, e.Message),
            SessionExpiredException e => (StatusCodes.Status410Gone, e.Code, e.Message),
            InvalidStateTransitionException e => (StatusCodes.Status409Conflict, e.Code, e.Message),
            SessionStateConflictException e => (StatusCodes.Status409Conflict, e.Code, e.Message),
            BusinessRuleException e => (StatusCodes.Status422UnprocessableEntity, e.Code, e.Message),
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "concurrent_modification",
                "The session was changed by another request. Fetch the latest state and retry."),
            BadHttpRequestException e => (StatusCodes.Status400BadRequest, "bad_request", e.Message),
            _ => (StatusCodes.Status500InternalServerError, "internal_error", "An unexpected error occurred."),
        };

        if (status >= 500) logger.LogError(exception, "Unhandled exception");
        else logger.LogInformation("Request rejected with {Status} {Code}", status, code);

        http.Response.StatusCode = status;
        var problem = new ProblemDetails { Status = status, Title = code, Detail = detail };
        problem.Extensions["code"] = code;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails = problem,
            Exception = exception,
        });
    }
}

public static class Problems
{
    public static IResult Validation(params (string Field, string Error)[] errors) =>
        Results.ValidationProblem(
            errors.GroupBy(e => e.Field).ToDictionary(g => g.Key, g => g.Select(e => e.Error).ToArray()),
            extensions: new Dictionary<string, object?> { ["code"] = "validation_failed" });
}
