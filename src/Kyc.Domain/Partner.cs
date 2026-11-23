namespace Kyc.Domain;

// A tenant (bank or fintech).
public class Partner
{
    private Partner() { } // EF

    // Slug, e.g. "demo-bank".
    public string Id { get; private set; } = default!;
    public string Name { get; private set; } = default!;
    public bool IsActive { get; private set; }

    // Days after the decision before images are purged.
    public int ImageRetentionDays { get; private set; }

    // Webhook delivery isn't built yet.
    public string? WebhookUrl { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public List<PartnerApiKey> ApiKeys { get; private set; } = [];

    public static Partner Create(string id, string name, int imageRetentionDays, string? webhookUrl, DateTimeOffset now)
    {
        if (imageRetentionDays < 1) throw new BusinessRuleException("invalid_retention", "Retention must be at least 1 day.");
        return new Partner
        {
            Id = id,
            Name = name,
            IsActive = true,
            ImageRetentionDays = imageRetentionDays,
            WebhookUrl = webhookUrl,
            CreatedAt = now,
        };
    }

    // Several keys allowed, for rotation.
    public PartnerApiKey AddApiKey(string keyHash, string keyPrefix, DateTimeOffset now)
    {
        var key = PartnerApiKey.Create(Id, keyHash, keyPrefix, now);
        ApiKeys.Add(key);
        return key;
    }
}

// Only the SHA-256 hash is stored; the prefix identifies a key.
public class PartnerApiKey
{
    private PartnerApiKey() { } // EF

    public Guid Id { get; private set; }
    public string PartnerId { get; private set; } = default!;
    public string KeyHash { get; private set; } = default!;
    public string KeyPrefix { get; private set; } = default!;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    internal static PartnerApiKey Create(string partnerId, string keyHash, string keyPrefix, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        PartnerId = partnerId,
        KeyHash = keyHash,
        KeyPrefix = keyPrefix,
        CreatedAt = now,
    };

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;
}
