using Kyc.Domain;
using Kyc.Infrastructure.Persistence;
using Kyc.Infrastructure.Security;

namespace Kyc.Api.Endpoints;

public sealed class KycApiOptions
{
    public const string Section = "Kyc";
    public int SessionLifetimeMinutes { get; set; } = 24 * 60;
    public int HandoffTtlSeconds { get; set; } = 300;
    /// <summary>Front-end page that reads the token from the URL fragment and calls /v1/handoff/redeem.</summary>
    public string HandoffBaseUrl { get; set; } = "http://localhost:3000/verify/continue";
}

internal static class AccessTokens
{
    /// <summary>Returns the plaintext token once. Only the hash is stored.</summary>
    public static (string Token, SessionAccessToken Entity) Issue(KycDbContext db, VerificationSession session,
        Channel channel, bool viaHandoff, string? ip, string? userAgent, string? fingerprint, DateTimeOffset now)
    {
        var token = Tokens.Generate();
        var entity = SessionAccessToken.Create(session.Id, Tokens.Hash(token), channel, viaHandoff, ip, userAgent,
            fingerprint, session.ExpiresAt, now);
        db.AccessTokens.Add(entity);
        return (token, entity);
    }
}
