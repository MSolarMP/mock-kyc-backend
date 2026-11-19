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

// One deployable, two roles: "api" serves HTTP, "worker" runs the workflow. They scale independently in production.
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

// ---- Errors ----
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemExceptionHandler>();

// ---- AuthN / AuthZ ----
// The default scheme forwards on the credential presented, so UseAuthentication fills HttpContext.User *before*
// rate limiting, and limits can be per session / per client rather than per IP (which would lump together
// everyone behind a carrier NAT).
builder.Services.AddAuthentication(AuthSchemes.Default)
    .AddPolicyScheme(AuthSchemes.Default, "API key or session token", o => o.ForwardDefaultSelector = ctx =>
        ctx.Request.Headers.ContainsKey("X-Api-Key") ? AuthSchemes.ApiKey : AuthSchemes.SessionToken)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(AuthSchemes.ApiKey, null)
    .AddScheme<AuthenticationSchemeOptions, SessionTokenAuthenticationHandler>(AuthSchemes.SessionToken, null);
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Policies.Partner, p => p.AddAuthenticationSchemes(AuthSchemes.ApiKey).RequireRole(Policies.Partner))
    .AddPolicy(Policies.Customer, p => p.AddAuthenticationSchemes(AuthSchemes.SessionToken)
        .RequireClaim(KycClaims.SessionId));

// ---- Rate limiting (per instance; at scale this moves to the API gateway / a shared store) ----
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
            TokenLimit = 60, TokensPerPeriod = 60, ReplenishmentPeriod = TimeSpan.FromMinutes(1),
        }));
    o.AddPolicy("partner", ctx => RateLimitPartition.GetTokenBucketLimiter(
        ctx.User.FindFirstValue(KycClaims.PartnerId) ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 600, TokensPerPeriod = 600, ReplenishmentPeriod = TimeSpan.FromMinutes(1),
        }));
});

// Behind a load balancer: trust X-Forwarded-For so audit and fraud signals record the real client IP.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear(); // Demo only: in production, restrict to the load balancer's address range.
    o.KnownProxies.Clear();
});

// ---- Observability ----
builder.Logging.Configure(o => o.ActivityTrackingOptions =
    ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService($"kyc-{string.Join("-", roles.Order())}"))
    .WithMetrics(m => m
        .AddMeter(KycMetrics.MeterName)
        .AddAspNetCoreInstrumentation()
        .AddPrometheusExporter());
// Distributed tracing is designed but not wired up: workflow spans already use KycMetrics.ActivitySource, so enabling
// it is .WithTracing(t => t.AddSource(...).AddAspNetCoreInstrumentation().AddOtlpExporter()) plus a collector.
builder.Services.AddHealthChecks()
    .AddDbContextCheck<KycDbContext>("database", tags: ["ready"]);

// ---- OpenAPI ----
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
        Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header, Name = "X-Api-Key",
        Description = "Partner key, e.g. dev-partner-key. Review endpoints also need an X-Reviewer-Id header.",
    });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http, Scheme = "bearer",
        Description = "Customer device token (clientToken from session creation or handoff redeem)",
    });
    c.AddSecurityRequirement(doc => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("ApiKey", doc)] = [],
        [new OpenApiSecuritySchemeReference("Bearer", doc)] = [],
    });
});

var app = builder.Build();

await InitialiseAsync(app);

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseAuthentication();
app.UseRateLimiter();   // after authentication, so partitions can use the session / client identity
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

app.Run();

static async Task InitialiseAsync(WebApplication app)
{
    var config = app.Configuration;
    using var scope = app.Services.CreateScope();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    // Dev/demo convenience. In production, migrations run as a separate pipeline step before rollout.
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
