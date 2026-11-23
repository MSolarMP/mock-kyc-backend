using Kyc.Domain;
using Kyc.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Kyc.Infrastructure.Persistence;

public sealed class PartnerSeedOptions
{
    public const string Section = "Partners";
    public bool SeedOnStartup { get; set; }
    public List<SeedPartner> Seed { get; set; } = [];

    public sealed class SeedPartner
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        // Plaintext key, dev/test only.
        public string ApiKey { get; set; } = "";
        public int ImageRetentionDays { get; set; } = 30;
        public string? WebhookUrl { get; set; }
    }
}

// Dev/test only: creates the configured partners and keys if missing. Safe to rerun.
public static class PartnerSeeder
{
    public static async Task SeedAsync(KycDbContext db, PartnerSeedOptions options, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var seed in options.Seed.Where(s => s.Id.Length > 0 && s.ApiKey.Length > 0))
        {
            var partner = await db.Partners.Include(p => p.ApiKeys).SingleOrDefaultAsync(p => p.Id == seed.Id, ct);
            if (partner is null)
            {
                partner = Partner.Create(seed.Id, seed.Name.Length > 0 ? seed.Name : seed.Id, seed.ImageRetentionDays,
                    seed.WebhookUrl, now);
                db.Partners.Add(partner);
            }

            var hash = Tokens.Hash(seed.ApiKey);
            if (partner.ApiKeys.All(k => k.KeyHash != hash))
                db.PartnerApiKeys.Add(partner.AddApiKey(hash, seed.ApiKey[..Math.Min(6, seed.ApiKey.Length)], now));
        }
        await db.SaveChangesAsync(ct);
    }
}
