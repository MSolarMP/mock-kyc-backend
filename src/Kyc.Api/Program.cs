using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Kyc.Api.Auth;
using Kyc.Api.Endpoints;
using Kyc.Api.Infrastructure;
using Kyc.Infrastructure;
using Kyc.Infrastructure.Observability;
using Kyc.Infrastructure.Persistence;
using Kyc.Infrastructure.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

var builder = WebApplication.CreateBuilder(args);

// Same binary runs as api, worker, or both.
var roles = (builder.Configuration["Kyc:Roles"] ?? "api,worker")
    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
    .ToHashSet(StringComparer.OrdinalIgnoreCase);
var isApi = roles.Contains("api");
var isWorker = roles.Contains("worker");

builder.Services.AddKycInfrastructure(builder.Configuration);
if (isWorker) builder.Services.AddKycWorker();

builder.Services.Configure<KycApiOptions>(builder.Configuration.GetSection(KycApiOptions.Section));
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemExceptionHandler>();

// Pick the auth scheme from the request headers.
builder.Services.AddAuthentication(AuthSchemes.Default)
    .AddPolicyScheme(AuthSchemes.Default, "API key or session token", o => o.ForwardDefaultSelector = ctx =>
        ctx.Request.Headers.ContainsKey("X-Api-Key") ? AuthSchemes.ApiKey : AuthSchemes.SessionToken)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(AuthSchemes.ApiKey, null)
    .AddScheme<AuthenticationSchemeOptions, SessionTokenAuthenticationHandler>(AuthSchemes.SessionToken, null);
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.Partner, p => p.AddAuthenticationSchemes(AuthSchemes.ApiKey).RequireRole(Policies.Partner))
    .AddPolicy(Policies.Customer, p => p.AddAuthenticationSchemes(AuthSchemes.SessionToken)
        .RequireClaim(KycClaims.SessionId));

// Per-instance rate limits.
var redeemPerMinute = builder.Configuration.GetValue("RateLimits:HandoffRedeemPerMinute", 10);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("handoff-redeem", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = redeemPerMinute, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("customer", ctx => RateLimitPartition.GetTokenBucketLimiter(
        ctx.User.FindFirstValue(KycClaims.SessionId) ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 60,
            TokensPerPeriod = 60,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
        }));
    o.AddPolicy("partner", ctx => RateLimitPartition.GetTokenBucketLimiter(
        ctx.User.FindFirstValue(KycClaims.PartnerId) ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 600,
            TokensPerPeriod = 600,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
        }));
});

// Trust X-Forwarded-For for the client IP.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear(); // Demo only. Restrict to the load balancer range in production.
    o.KnownProxies.Clear();
});

builder.Logging.Configure(o => o.ActivityTrackingOptions =
    ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService($"kyc-{string.Join("-", roles.Order())}"))
    .WithMetrics(m => m
        .AddMeter(KycMetrics.MeterName)
        .AddAspNetCoreInstrumentation()
        .AddPrometheusExporter());
// Tracing isn't wired up yet; needs .WithTracing() and a collector.
builder.Services.AddHealthChecks()
    .AddDbContextCheck<KycDbContext>("database", tags: ["ready"]);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "KYC Verification Platform API",
        Version = "v1",
        Description = "Partner (X-Api-Key), reviewer (X-Api-Key + X-Reviewer-Id) and customer-device (Bearer session token) APIs.",
    });
    c.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Name = "X-Api-Key",
        Description = "Partner key, e.g. dev-partner-key. Review endpoints also need an X-Reviewer-Id header.",
    });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        Description = "Customer device token (clientToken from session creation or handoff redeem)",
    });
    // Declared as a scheme only so Swagger's Authorize dialog shows a field for it.
    c.AddSecurityDefinition("ReviewerId", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.ApiKey,
        In = ParameterLocation.Header,
        Name = "X-Reviewer-Id",
        Description = "Reviewer's staff id, e.g. staff-1042. Only needed for /v1/reviews endpoints.",
    });
    c.AddSecurityRequirement(doc => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("ApiKey", doc)] = [],
        [new OpenApiSecuritySchemeReference("Bearer", doc)] = [],
        [new OpenApiSecuritySchemeReference("ReviewerId", doc)] = [],
    });
});

var app = builder.Build();

await InitialiseAsync(app);

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseAuthentication();
app.UseRateLimiter();   // after authentication
app.UseAuthorization();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });
app.MapPrometheusScrapingEndpoint("/metrics");

if (isApi)
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.DocumentTitle = "KYC Platform API");
    app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();
    app.MapPartnerEndpoints();
    app.MapClientEndpoints();
    app.MapReviewEndpoints();
}

app.Lifetime.ApplicationStarted.Register(() => PrintBanner(app, roles, isApi));

app.Run();

// Plain console output, not the JSON logger.
static void PrintBanner(WebApplication app, IEnumerable<string> roles, bool isApi)
{
    var urls = app.Urls
        .Select(u => u.Replace("://+", "://localhost").Replace("://*", "://localhost")
            .Replace("://0.0.0.0", "://localhost").Replace("://[::]", "://localhost"))
        .ToList();
    var baseUrl = urls.FirstOrDefault(u => u.StartsWith("http://")) ?? urls.FirstOrDefault() ?? "(unknown)";
    var db = new Npgsql.NpgsqlConnectionStringBuilder(app.Configuration.GetConnectionString("Kyc"));

    Console.WriteLine();
    Console.WriteLine($"  KYC service started ({string.Join(" + ", roles.Order())})");
    if (isApi)
    {
        Console.WriteLine($"  Swagger:      {baseUrl}/swagger");
        Console.WriteLine($"  Health:       {baseUrl}/health/ready");
        Console.WriteLine($"  Metrics:      {baseUrl}/metrics");
    }
    else
    {
        Console.WriteLine("  Processing jobs (no public API)");
    }
    Console.WriteLine($"  Database:     {db.Host}:{db.Port}/{db.Database}");
    Console.WriteLine($"  Storage (S3): {app.Configuration["Storage:PublicServiceUrl"] ?? app.Configuration["Storage:ServiceUrl"]} bucket={app.Configuration["Storage:Bucket"]}");
    Console.WriteLine();
}

static async Task InitialiseAsync(WebApplication app)
{
    var config = app.Configuration;
    using var scope = app.Services.CreateScope();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    // Demo convenience. Production runs migrations as a separate deploy step.
    if (config.GetValue("Database:MigrateOnStartup", false))
    {
        logger.LogInformation("Applying database migrations");
        await scope.ServiceProvider.GetRequiredService<KycDbContext>().Database.MigrateAsync();
    }
    var partners = config.GetSection(PartnerSeedOptions.Section).Get<PartnerSeedOptions>();
    if (partners is { SeedOnStartup: true })
        await PartnerSeeder.SeedAsync(scope.ServiceProvider.GetRequiredService<KycDbContext>(), partners,
            TimeProvider.System.GetUtcNow(), CancellationToken.None);
    if (config.GetValue("Storage:EnsureBucketOnStartup", false))
        await scope.ServiceProvider.GetRequiredService<IObjectStorage>().EnsureBucketAsync(CancellationToken.None);
}

public partial class Program;
