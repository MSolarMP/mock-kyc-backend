namespace Kyc.Domain;

/// <summary>
/// A tenant: one bank or fintech that creates verification sessions. Onboarding a new partner is a row insert, not a
/// deploy. A partner only ever sees its own sessions.
/// </summary>
public class Partner
{
    private Partner() { } // EF

    /// <summary>Stable slug, e.g. "demo-bank". Stored on every session it creates.</summary>
    public string Id { get; private set; } = default!;
    public string Name { get; private set; } = default!;
    public bool IsActive { get; private set; }

    /// <summary>
    /// Days after a final decision before this partner's document and selfie images are purged. Configurable per
    /// partner because the bank, as the FICA accountable institution, decides how long records are needed.
    /// </summary>
    public int ImageRetentionDays { get; private set; }

    /// <summary>Where decision webhooks would be delivered. Webhook delivery is designed, not built (see docs).</summary>
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

    /// <summary>Adds a key. A partner can hold several, so keys rotate without downtime (add new, switch, revoke old).</summary>
    public PartnerApiKey AddApiKey(string keyHash, string keyPrefix, DateTimeOffset now)
    {
        var key = PartnerApiKey.Create(Id, keyHash, keyPrefix, now);
        ApiKeys.Add(key);
        return key;
    }
}

/// <summary>An API key for a partner. Only the SHA-256 hash is stored; the prefix helps support identify a key safely.</summary>
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
