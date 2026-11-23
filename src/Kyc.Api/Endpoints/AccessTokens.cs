using Kyc.Domain;
using Kyc.Infrastructure.Persistence;
using Kyc.Infrastructure.Security;

namespace Kyc.Api.Endpoints;

// Issues a session's device access token: the plaintext is returned once, only the hash is stored.
internal static class AccessTokens
{
    // Returns the plaintext token once. Only the hash is stored.
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
