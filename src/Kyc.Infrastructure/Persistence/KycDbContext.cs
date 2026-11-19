using System.Text.Json;
using Kyc.Domain;
using Kyc.Infrastructure.Common;
using Microsoft.EntityFrameworkCore;

namespace Kyc.Infrastructure.Persistence;

public class KycDbContext(DbContextOptions<KycDbContext> options, IRequestContext requestContext, TimeProvider clock)
    : DbContext(options)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public DbSet<Partner> Partners => Set<Partner>();
    public DbSet<PartnerApiKey> PartnerApiKeys => Set<PartnerApiKey>();
    public DbSet<VerificationSession> Sessions => Set<VerificationSession>();
    public DbSet<Artifact> Artifacts => Set<Artifact>();
    public DbSet<ProviderCheck> ProviderChecks => Set<ProviderCheck>();
    public DbSet<Decision> Decisions => Set<Decision>();
    public DbSet<HandoffToken> HandoffTokens => Set<HandoffToken>();
    public DbSet<SessionAccessToken> AccessTokens => Set<SessionAccessToken>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<WorkflowJob> WorkflowJobs => Set<WorkflowJob>();

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        // Store enums as readable strings: audit queries and ad-hoc investigations shouldn't need a lookup table.
        b.Properties<SessionStatus>().HaveConversion<string>().HaveMaxLength(32);
        b.Properties<Channel>().HaveConversion<string>().HaveMaxLength(16);
        b.Properties<ArtifactType>().HaveConversion<string>().HaveMaxLength(32);
        b.Properties<ArtifactStatus>().HaveConversion<string>().HaveMaxLength(16);
        b.Properties<CheckType>().HaveConversion<string>().HaveMaxLength(32);
        b.Properties<CheckOutcome>().HaveConversion<string>().HaveMaxLength(16);
        b.Properties<DecisionOutcome>().HaveConversion<string>().HaveMaxLength(16);
        b.Properties<ActorType>().HaveConversion<string>().HaveMaxLength(16);
        b.Properties<JobStatus>().HaveConversion<string>().HaveMaxLength(16);
        b.Properties<JobType>().HaveConversion<string>().HaveMaxLength(32);
    }

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<Partner>(e =>
        {
            e.ToTable("partners");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.WebhookUrl).HasMaxLength(500);
            e.HasMany(x => x.ApiKeys).WithOne().HasForeignKey(x => x.PartnerId);
        });

        m.Entity<PartnerApiKey>(e =>
        {
            e.ToTable("partner_api_keys");
            e.HasKey(x => x.Id);
            e.Property(x => x.PartnerId).HasMaxLength(64);
            e.Property(x => x.KeyHash).HasMaxLength(64);
            e.Property(x => x.KeyPrefix).HasMaxLength(16);
            // Auth looks keys up by hash on every partner request.
            e.HasIndex(x => x.KeyHash).IsUnique();
        });

        m.Entity<VerificationSession>(e =>
        {
            e.ToTable("verification_sessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.ApplicantReference).HasMaxLength(128);
            e.Property(x => x.PartnerId).HasMaxLength(64);
            e.Property(x => x.IdempotencyKey).HasMaxLength(128);
            e.Property(x => x.IdempotencyRequestHash).HasMaxLength(64);
            e.Property(x => x.CreatedIp).HasMaxLength(64);
            e.Property(x => x.CreatedUserAgent).HasMaxLength(512);
            e.Property(x => x.DeviceFingerprint).HasMaxLength(128);
            e.Property(x => x.DocumentNumberHash).HasMaxLength(64);
            e.Property(x => x.Version).IsRowVersion(); // Maps to Postgres xmin
            e.Ignore(x => x.PendingEvents);
            e.Ignore(x => x.IsTerminal);
            e.Ignore(x => x.ExpectedArtifact);

            e.HasIndex(x => new { x.PartnerId, x.IdempotencyKey }).IsUnique();
            e.HasIndex(x => new { x.PartnerId, x.ApplicantReference });
            e.HasOne<Partner>().WithMany().HasForeignKey(x => x.PartnerId).OnDelete(DeleteBehavior.Restrict);
            // Review queue, expiry sweeps, dashboards
            e.HasIndex(x => new { x.Status, x.UpdatedAt });
            // Fraud: same document seen across sessions
            e.HasIndex(x => x.DocumentNumberHash).HasFilter("document_number_hash IS NOT NULL");
            // Fraud: velocity per IP
            e.HasIndex(x => new { x.CreatedIp, x.CreatedAt });

            e.HasMany(x => x.Artifacts).WithOne().HasForeignKey(x => x.SessionId);
            e.HasMany(x => x.Checks).WithOne().HasForeignKey(x => x.SessionId);
            e.HasMany(x => x.Decisions).WithOne().HasForeignKey(x => x.SessionId);
        });

        m.Entity<Artifact>(e =>
        {
            e.ToTable("artifacts");
            e.HasKey(x => x.Id);
            e.Property(x => x.StorageKey).HasMaxLength(256);
            e.Property(x => x.ContentType).HasMaxLength(64);
            e.Property(x => x.DeclaredSha256).HasMaxLength(64);
            e.HasIndex(x => x.DeclaredSha256); // Fraud: the same image reused across sessions
            e.HasIndex(x => new { x.Status, x.UploadedAt });
        });

        m.Entity<ProviderCheck>(e =>
        {
            e.ToTable("provider_checks");
            e.HasKey(x => x.Id);
            e.Property(x => x.Provider).HasMaxLength(64);
            e.Property(x => x.ProviderReference).HasMaxLength(128);
            e.Property(x => x.Score).HasPrecision(5, 4);
            e.HasIndex(x => new { x.SessionId, x.Type });
            // Idempotency backstop: one completed check per artifact per type
            e.HasIndex(x => new { x.ArtifactId, x.Type }).IsUnique();
        });

        m.Entity<Decision>(e =>
        {
            e.ToTable("decisions");
            e.HasKey(x => x.Id);
            e.Property(x => x.ReviewerId).HasMaxLength(128);
            e.Property(x => x.RulesVersion).HasMaxLength(32);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.HasIndex(x => new { x.SessionId, x.DecidedAt });
        });

        m.Entity<HandoffToken>(e =>
        {
            e.ToTable("handoff_tokens");
            e.HasKey(x => x.Id);
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.Property(x => x.RedeemedIp).HasMaxLength(64);
            e.Property(x => x.RedeemedUserAgent).HasMaxLength(512);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.SessionId);
        });

        m.Entity<SessionAccessToken>(e =>
        {
            e.ToTable("session_access_tokens");
            e.HasKey(x => x.Id);
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.Property(x => x.UserAgent).HasMaxLength(512);
            e.Property(x => x.DeviceFingerprint).HasMaxLength(128);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.SessionId);
        });

        m.Entity<AuditEvent>(e =>
        {
            e.ToTable("audit_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.EventType).HasMaxLength(64);
            e.Property(x => x.ActorId).HasMaxLength(200);
            e.Property(x => x.CorrelationId).HasMaxLength(64);
            e.Property(x => x.IpAddress).HasMaxLength(64);
            e.Property(x => x.Data).HasColumnType("jsonb");
            e.HasIndex(x => new { x.SessionId, x.Id });
            e.HasIndex(x => new { x.EventType, x.OccurredAt });
        });

        // Guid ids are generated in the domain (UUIDv7: time-ordered, so B-tree inserts stay local). Telling EF the
        // ids are never store-generated means a new child added through a navigation (session.Artifacts.Add) is
        // tracked as Added, not mistaken for an existing row and UPDATEd.
        foreach (var entity in m.Model.GetEntityTypes())
        {
            var key = entity.FindPrimaryKey();
            if (key is { Properties: [{ ClrType: var t } keyProperty] } && t == typeof(Guid))
                keyProperty.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
        }

        m.Entity<WorkflowJob>(e =>
        {
            e.ToTable("workflow_jobs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.Property(x => x.LockedBy).HasMaxLength(128);
            e.Property(x => x.CorrelationId).HasMaxLength(64);
            // Partial index: the claim query only ever scans runnable work, so the queue stays fast as history grows.
            e.HasIndex(x => x.RunAfter).HasFilter("status IN ('Pending','Running')")
                .HasDatabaseName("ix_workflow_jobs_runnable");
            e.HasIndex(x => x.SessionId);
        });
    }

    /// <summary>
    /// Turns pending domain events into audit rows in the same
    /// transaction as the aggregate change. The trail is complete because no code path can change state
    /// without also writing the audit row.
    /// </summary>
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var sessions = ChangeTracker.Entries<VerificationSession>()
            .Select(e => e.Entity)
            .Where(s => s.PendingEvents.Count > 0)
            .ToList();

        var now = clock.GetUtcNow();
        foreach (var session in sessions)
        {
            foreach (var evt in session.PendingEvents)
            {
                var payload = JsonSerializer.Serialize(evt.Data ?? new { }, Json);
                AuditEvents.Add(new AuditEvent
                {
                    SessionId = evt.SessionId,
                    EventType = evt.EventType,
                    ActorType = requestContext.ActorType,
                    ActorId = requestContext.ActorId,
                    CorrelationId = requestContext.CorrelationId,
                    IpAddress = requestContext.IpAddress,
                    Data = payload,
                    OccurredAt = now,
                });
            }
        }

        var result = await base.SaveChangesAsync(cancellationToken);
        foreach (var session in sessions) session.ClearPendingEvents();
        return result;
    }
}
