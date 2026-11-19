using Kyc.Domain.Decisioning;
using Kyc.Infrastructure.Common;
using Kyc.Infrastructure.Maintenance;
using Kyc.Infrastructure.Observability;
using Kyc.Infrastructure.Outbox;
using Kyc.Infrastructure.Persistence;
using Kyc.Infrastructure.Providers;
using Kyc.Infrastructure.Providers.Mock;
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
            // No EnableRetryOnFailure: it forbids explicit transactions (used by the outbox relay), and transient
            // DB failures already surface as job retries (worker) or a 503 the client can retry (API).
            .UseNpgsql(config.GetConnectionString("Kyc"))
            .UseSnakeCaseNamingConvention());

        services.Configure<SecurityOptions>(config.GetSection(SecurityOptions.Section));
        services.AddSingleton<FieldProtector>();
        services.AddSingleton<IdentifierHasher>();

        services.Configure<StorageOptions>(config.GetSection(StorageOptions.Section));
        services.AddSingleton<IObjectStorage, S3ObjectStorage>();

        services.AddSingleton<KycMetrics>();

        services.AddSingleton(config.GetSection("Decisioning").Get<DecisionPolicy>() ?? new DecisionPolicy());
        services.AddSingleton<DecisionEngine>();

        AddProviders(services, config);

        services.Configure<WorkerOptions>(config.GetSection(WorkerOptions.Section));
        services.Configure<RetentionOptions>(config.GetSection(RetentionOptions.Section));
        services.AddScoped<IJobHandler, ValidateDocumentHandler>();
        services.AddScoped<IJobHandler, CheckLivenessHandler>();
        services.AddScoped<IJobHandler, MatchFaceHandler>();
        services.AddScoped<IJobHandler, AssessRiskAndDecideHandler>();
        services.AddSingleton<IEventPublisher, LoggingEventPublisher>();

        return services;
    }

    /// <summary>Background processing: the workflow job processor, outbox relay and maintenance sweeps.</summary>
    public static IServiceCollection AddKycWorker(this IServiceCollection services)
    {
        services.AddHostedService<JobProcessor>();
        services.AddHostedService<OutboxPublisher>();
        services.AddHostedService<MaintenanceService>();
        return services;
    }

    /// <summary>
    /// Picks an adapter per capability from config ("Providers:Document" = "mock" | "vendor-x" ...), so each capability can
    /// use a different vendor. Every adapter is wrapped in the same resilience decorator.
    /// </summary>
    private static void AddProviders(IServiceCollection services, IConfiguration config)
    {
        services.Configure<MockProviderOptions>(config.GetSection(MockProviderOptions.Section));
        var resilience = config.GetSection(ProviderResilienceOptions.Section).Get<ProviderResilienceOptions>()
                         ?? new ProviderResilienceOptions();

        var document = config["Providers:Document"] ?? "mock";
        var liveness = config["Providers:Liveness"] ?? "mock";
        var faceMatch = config["Providers:FaceMatch"] ?? "mock";
        if (document != "mock" || liveness != "mock" || faceMatch != "mock")
            throw new InvalidOperationException("Only the 'mock' provider adapters are implemented in this build.");

        services.AddSingleton<MockDocumentProvider>();
        services.AddSingleton<MockLivenessProvider>();
        services.AddSingleton<MockFaceMatchProvider>();

        services.AddSingleton<IDocumentVerificationProvider>(sp =>
        {
            var inner = sp.GetRequiredService<MockDocumentProvider>();
            return new ResilientDocumentProvider(inner, ProviderResilience.Build(inner.Name, resilience));
        });
        services.AddSingleton<ILivenessProvider>(sp =>
        {
            var inner = sp.GetRequiredService<MockLivenessProvider>();
            return new ResilientLivenessProvider(inner, ProviderResilience.Build(inner.Name, resilience));
        });
        services.AddSingleton<IFaceMatchProvider>(sp =>
        {
            var inner = sp.GetRequiredService<MockFaceMatchProvider>();
            return new ResilientFaceMatchProvider(inner, ProviderResilience.Build(inner.Name, resilience));
        });
    }
}
