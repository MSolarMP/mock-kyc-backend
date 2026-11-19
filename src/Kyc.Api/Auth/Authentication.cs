using System.Security.Claims;
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
    /// <summary>A partner system, or the partner's back office acting for one of its reviewers.</summary>
    public const string Partner = "partner";
    public const string Customer = "customer";
}

public static class KycClaims
{
    public const string PartnerId = "kyc:partner_id";
    public const string SessionId = "kyc:session_id";
    public const string AccessTokenId = "kyc:access_token_id";
    public const string Channel = "kyc:channel";
}

/// <summary>
/// Server-to-server auth for partner systems. Keys live hashed in <c>partner_api_keys</c>; a request's key is hashed
/// and looked up by that hash (unique index), so the plaintext is never stored or compared.
/// Production would put OAuth2 client credentials or mTLS in front of this (see docs).
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    KycDbContext db,
    RequestContext requestContext)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Api-Key", out var provided) || string.IsNullOrEmpty(provided))
            return AuthenticateResult.NoResult();

        var hash = Tokens.Hash(provided.ToString());
        var partnerId = await db.PartnerApiKeys.AsNoTracking()
            .Where(k => k.KeyHash == hash && k.RevokedAt == null)
            .Join(db.Partners.Where(p => p.IsActive), k => k.PartnerId, p => p.Id, (k, p) => p.Id)
            .SingleOrDefaultAsync();
        if (partnerId is null)
            return AuthenticateResult.Fail("Invalid API key.");

        requestContext.SetActor(ActorType.Partner, $"partner:{partnerId}");
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, partnerId),
            new Claim(KycClaims.PartnerId, partnerId),
            new Claim(ClaimTypes.Role, Policies.Partner),
        ], Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
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
    public static string PartnerId(this ClaimsPrincipal user) =>
        user.FindFirstValue(KycClaims.PartnerId) ?? throw new UnauthorizedAccessException();

    public static Guid SessionId(this ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(KycClaims.SessionId) ?? throw new UnauthorizedAccessException());

    public static Guid AccessTokenId(this ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirstValue(KycClaims.AccessTokenId) ?? throw new UnauthorizedAccessException());
}
