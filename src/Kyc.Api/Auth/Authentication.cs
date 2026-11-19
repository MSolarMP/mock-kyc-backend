using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Kyc.Domain;
using Kyc.Infrastructure.Common;
using Kyc.Infrastructure.Persistence;
using Kyc.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kyc.Api.Auth;

public static class AuthSchemes
{
    public const string Default = "Default";
    public const string ApiKey = "ApiKey";
    public const string SessionToken = "SessionToken";
}

public static class Policies
{
    public const string Partner = "partner";
    public const string Reviewer = "reviewer";
    public const string PartnerOrReviewer = "partner-or-reviewer";
    public const string Customer = "customer";
}

public static class KycClaims
{
    public const string ClientId = "kyc:client_id";
    public const string SessionId = "kyc:session_id";
    public const string AccessTokenId = "kyc:access_token_id";
    public const string Channel = "kyc:channel";
}

public sealed class ApiClientOptions
{
    public const string Section = "ApiClients";
    public string ClientId { get; set; } = "";
    public string Key { get; set; } = "";
    public string[] Roles { get; set; } = [];
}

/// <summary>
/// Server-to-server auth for partner/onboarding systems and back-office tools.
/// Stand-in for OAuth2 client credentials or mTLS through an API gateway. Keys come from config/secrets here.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration config,
    RequestContext requestContext)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Api-Key", out var provided) || string.IsNullOrEmpty(provided))
            return Task.FromResult(AuthenticateResult.NoResult());

        var clients = config.GetSection(ApiClientOptions.Section).Get<ApiClientOptions[]>() ?? [];
        var providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(provided.ToString()));
        // Constant-time comparison of hashes, so timing doesn't leak how much of a key was right.
        var client = clients.FirstOrDefault(c =>
            CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(c.Key)), providedHash));
        if (client is null)
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, client.ClientId),
            new(KycClaims.ClientId, client.ClientId),
        };
        claims.AddRange(client.Roles.Select(r => new Claim(ClaimTypes.Role, r)));

        requestContext.SetActor(client.Roles.Contains(Policies.Reviewer) ? ActorType.Reviewer : ActorType.ApiClient,
            client.ClientId);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}

/// <summary>
/// Customer device auth: an opaque bearer token bound to one session. The session id comes from the token, never
/// from the URL, so a customer can't name a different session.
/// </summary>
public sealed class SessionTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    KycDbContext db,
    TimeProvider clock,
    RequestContext requestContext)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var token = header["Bearer ".Length..].Trim();
        if (token.Length is < 20 or > 100) return AuthenticateResult.Fail("Malformed token.");

        var hash = Tokens.Hash(token);
        var now = clock.GetUtcNow();
        var accessToken = await db.AccessTokens.AsNoTracking()
            .SingleOrDefaultAsync(t => t.TokenHash == hash && t.RevokedAt == null && t.ExpiresAt > now);
        if (accessToken is null) return AuthenticateResult.Fail("Invalid or expired token.");

        requestContext.SetActor(ActorType.Customer, $"token:{accessToken.Id}");
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, accessToken.Id.ToString()),
            new Claim(KycClaims.SessionId, accessToken.SessionId.ToString()),
            new Claim(KycClaims.AccessTokenId, accessToken.Id.ToString()),
            new Claim(KycClaims.Channel, accessToken.Channel.ToString()),
        ], Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}

public static class ClaimsPrincipalExtensions
{
    public static string ClientId(this ClaimsPrincipal user) =>
        user.FindFirstValue(KycClaims.ClientId) ?? throw new UnauthorizedAccessException();

    public static bool IsReviewer(this ClaimsPrincipal user) => user.IsInRole(Policies.Reviewer);

    public static Guid SessionId(this ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(KycClaims.SessionId) ?? throw new UnauthorizedAccessException());

    public static Guid AccessTokenId(this ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(KycClaims.AccessTokenId) ?? throw new UnauthorizedAccessException());
}
