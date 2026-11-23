namespace Kyc.Domain;

// Opaque bearer token bound to one session (not a JWT).
public class SessionAccessToken
{
    private SessionAccessToken() { }

    public Guid Id { get; private set; }
    public Guid SessionId { get; private set; }
    public string TokenHash { get; private set; } = default!;
    public Channel Channel { get; private set; }
    // True if issued by redeeming a QR handoff.
    public bool ViaHandoff { get; private set; }
    public string? IpAddress { get; private set; }
    public string? UserAgent { get; private set; }
    public string? DeviceFingerprint { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    public static SessionAccessToken Create(Guid sessionId, string tokenHash, Channel channel, bool viaHandoff,
        string? ip, string? userAgent, string? fingerprint, DateTimeOffset expiresAt, DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(now),
            SessionId = sessionId,
            TokenHash = tokenHash,
            Channel = channel,
            ViaHandoff = viaHandoff,
            IpAddress = ip,
            UserAgent = userAgent,
            DeviceFingerprint = fingerprint,
            CreatedAt = now,
            ExpiresAt = expiresAt,
        };
}
