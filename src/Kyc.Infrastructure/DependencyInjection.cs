using Kyc.Domain.Decisioning;
using Kyc.Infrastructure.Common;
using Kyc.Infrastructure.Maintenance;
using Kyc.Infrastructure.Observability;
using Kyc.Infrastructure.Persistence;
using Kyc.Infrastructure.Providers;
using Kyc.Infrastructure.Providers.VendorA;
using Kyc.Infrastructure.Security;
using Kyc.Infrastructure.Storage;
using Kyc.Infrastructure.Workflow;
using Kyc.Infrastructure.Workflow.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Kyc.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddKycInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<RequestContext>();
        services.AddScoped<IRequestContext>(sp => sp.GetRequiredService<RequestContext>());

        services.AddDbContext<KycDbContext>(o => o
            // No EnableRetryOnFailure: it forbids explicit transactions, and transient
            // DB failures already surface as job retries (worker) or a 503 the client can retry (API).
            .UseNpgsql(config.GetConnectionString("Kyc"))
            .UseSnakeCaseNamingConvention());

        services.Configure<SecurityOptions>(config.GetSection(SecurityOptions.Section));
        services.AddSingleton<FieldProtector>();
        services.AddSingleton<IdentifierHasher>();

        services.Configure<StorageOptions>(config.GetSection(StorageOptions.Section));
        services.AddSingleton<IObjectStorage, S3ObjectStorage>();

        services.AddSingleton<KycMetrics>();

        AddProviders(services, config);
        services.AddSingleton<DecisionEngine>();

        services.Configure<WorkerOptions>(config.GetSection(WorkerOptions.Section));
        services.Configure<RetentionOptions>(config.GetSection(RetentionOptions.Section));
        services.AddScoped<IJobHandler, ValidateDocumentHandler>();
        services.AddScoped<IJobHandler, CheckLivenessHandler>();
        services.AddScoped<IJobHandler, MatchFaceHandler>();
        services.AddScoped<IJobHandler, AssessRiskAndDecideHandler>();

        return services;
    }

    /// <summary>Background processing: the workflow job processor and maintenance sweeps.</summary>
    public static IServiceCollection AddKycWorker(this IServiceCollection services)
    {
        services.AddHostedService<JobProcessor>();
        services.AddHostedService<MaintenanceService>();
        return services;
    }

    /// <summary>
    /// Picks the vendor adapter per capability from config ("Providers:Document" = "vendor-a" | "vendor-b" ...), wraps each
    /// in the same resilience decorator (timeout + circuit breaker), and takes the decision thresholds from the vendor
    /// that produces the scores. Adding Vendor B = a new adapter folder + a case here + config.
    /// </summary>
    private static void AddProviders(IServiceCollection services, IConfiguration config)
    {
        var resilience = config.GetSection(ProviderResilienceOptions.Section).Get<ProviderResilienceOptions>()
                         ?? new ProviderResilienceOptions();
        foreach (var capability in new[] { "Document", "Liveness", "FaceMatch" })
        {
            var vendor = config[$"Providers:{capability}"] ?? VendorAOptions.Name;
            if (vendor != VendorAOptions.Name)
                throw new InvalidOperationException($"Unknown provider '{vendor}' for {capability}. Only '{VendorAOptions.Name}' is implemented.");
        }

        services.Configure<VendorAOptions>(config.GetSection(VendorAOptions.Section));
        services.AddSingleton<IVendorAClient, SandboxVendorAClient>();
        services.AddSingleton<VendorAAdapter>();

        services.AddSingleton<IDocumentVerificationProvider>(sp => new ResilientDocumentProvider(
            sp.GetRequiredService<VendorAAdapter>(), ProviderResilience.Build(VendorAOptions.Name + ":document", resilience)));
        services.AddSingleton<ILivenessProvider>(sp => new ResilientLivenessProvider(
            sp.GetRequiredService<VendorAAdapter>(), ProviderResilience.Build(VendorAOptions.Name + ":liveness", resilience)));
        services.AddSingleton<IFaceMatchProvider>(sp => new ResilientFaceMatchProvider(
            sp.GetRequiredService<VendorAAdapter>(), ProviderResilience.Build(VendorAOptions.Name + ":facematch", resilience)));

        // Business rules (velocity limits, rules version) come from "Decisioning"; score thresholds from the vendor.
        services.AddSingleton(sp =>
        {
            var policy = config.GetSection("Decisioning").Get<DecisionPolicy>() ?? new DecisionPolicy();
            var t = sp.GetRequiredService<IOptions<VendorAOptions>>().Value.Thresholds;
            return policy with
            {
                FaceMatchApproveThreshold = t.FaceMatchApprove,
                FaceMatchRejectThreshold = t.FaceMatchReject,
                LivenessApproveThreshold = t.LivenessApprove,
            };
        });
    }
}
