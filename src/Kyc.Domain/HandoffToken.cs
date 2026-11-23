namespace Kyc.Domain;

// Single-use token in the QR code for moving to another device.
public class HandoffToken
{
    private HandoffToken() { }

    public Guid Id { get; private set; }
    public Guid SessionId { get; private set; }
    // Hash only.
    public string TokenHash { get; private set; } = default!;
    public Guid IssuedByTokenId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? RedeemedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public string? RedeemedIp { get; private set; }
    public string? RedeemedUserAgent { get; private set; }

    public static HandoffToken Create(Guid sessionId, string tokenHash, Guid issuedBy, TimeSpan ttl, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        SessionId = sessionId,
        TokenHash = tokenHash,
        IssuedByTokenId = issuedBy,
        CreatedAt = now,
        ExpiresAt = now.Add(ttl),
    };

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;
}
