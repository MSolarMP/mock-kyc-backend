using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Mvc.Testing;
using Testcontainers.PostgreSql;

namespace Kyc.IntegrationTests;

/// <summary>
/// Real Postgres and SeaweedFS in containers, with the full app (api + worker) in-process.
/// Nothing is mocked. Vendors use Vendor A's sandbox client.
/// </summary>
public sealed class KycApiFixture : IAsyncLifetime
{
    public const string PartnerKey = "it-partner-key";
    public const string OtherPartnerKey = "it-other-partner-key";
    public const string ReviewerId = "staff-1042";

    private const string S3Config = """
        {"identities":[{"name":"it","credentials":[{"accessKey":"it-access","secretKey":"it-secret"}],
          "actions":["Admin","Read","Write","List","Tagging"]}]}
        """;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    private readonly IContainer _s3 = new ContainerBuilder("chrislusf/seaweedfs:3.97")
        .WithCommand("server", "-dir=/data", "-s3", "-s3.port=8333", "-s3.config=/etc/seaweedfs/s3.json",
            "-master.volumeSizeLimitMB=64")
        .WithResourceMapping(Encoding.UTF8.GetBytes(S3Config), "/etc/seaweedfs/s3.json")
        .WithPortBinding(8333, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8333).ForPath("/healthz")))
        .Build();

    public WebApplicationFactory<Program> Factory { get; private set; } = default!;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _s3.StartAsync());
        var s3Url = $"http://{_s3.Hostname}:{_s3.GetMappedPublicPort(8333)}";

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            var settings = new Dictionary<string, string>
            {
                ["ConnectionStrings:Kyc"] = _postgres.GetConnectionString(),
                ["Kyc:Roles"] = "api,worker",
                ["Database:MigrateOnStartup"] = "true",
                ["Storage:ServiceUrl"] = s3Url,
                ["Storage:PublicServiceUrl"] = s3Url,
                ["Storage:AccessKey"] = "it-access",
                ["Storage:SecretKey"] = "it-secret",
                ["Storage:Bucket"] = "kyc-it",
                ["Storage:EnsureBucketOnStartup"] = "true",
                ["Worker:PollIntervalMs"] = "100",
                ["Providers:VendorA:SandboxLatencyMs"] = "0",
                ["Retention:SweepIntervalSeconds"] = "3600",
                ["RateLimits:HandoffRedeemPerMinute"] = "1000",
                ["Partners:SeedOnStartup"] = "true",
                ["Partners:Seed:0:Id"] = "partner-a",
                ["Partners:Seed:0:ApiKey"] = PartnerKey,
                ["Partners:Seed:1:Id"] = "partner-b",
                ["Partners:Seed:1:ApiKey"] = OtherPartnerKey,
                ["Logging:Console:FormatterName"] = "simple",
                ["Logging:LogLevel:Default"] = "Warning",
            };
            foreach (var (k, v) in settings) b.UseSetting(k, v);
        });
        _ = Factory.Server; // boot the app (migrations, bucket)
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _s3.DisposeAsync().AsTask());
    }

    public HttpClient Partner(string key = PartnerKey)
    {
        var c = Factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Api-Key", key);
        return c;
    }

    /// <summary>Partner A's back office acting for one of its reviewers.</summary>
    public HttpClient Reviewer(string reviewerId = ReviewerId)
    {
        var c = Partner();
        c.DefaultRequestHeaders.Add("X-Reviewer-Id", reviewerId);
        return c;
    }

    public HttpClient Customer(string token)
    {
        var c = Factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    public HttpClient Anonymous() => Factory.CreateClient();

    // Journey helpers

    public async Task<(Guid SessionId, string Token)> CreateSessionAsync(string? idempotencyKey = null, string? ip = null)
    {
        using var partner = Partner();
        using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/verification-sessions")
        {
            Content = JsonContent.Create(new
            {
                applicantReference = $"app-{Guid.NewGuid():N}",
                channel = "Web",
                customer = new { ipAddress = ip ?? $"203.0.113.{Random.Shared.Next(1, 254)}" },
            }),
        };
        req.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString("N"));
        var res = await partner.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>(Json);
        return (body.GetProperty("sessionId").GetGuid(), body.GetProperty("clientToken").GetString()!);
    }

    public static byte[] Image(params string[] markers) =>
        [0xFF, 0xD8, 0xFF, 0xE0, .. Encoding.ASCII.GetBytes($"it {Guid.NewGuid():N} " + string.Join(" ", markers.Select(m => "KYC_MOCK:" + m)))];

    public async Task<HttpResponseMessage> UploadAsync(string token, string type, byte[] bytes, bool skipPut = false)
    {
        using var customer = Customer(token);
        var slot = await customer.PostAsJsonAsync("/v1/client/session/uploads", new
        {
            type,
            contentType = "image/jpeg",
            sizeBytes = bytes.Length,
            sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
        });
        slot.EnsureSuccessStatusCode();
        var s = await slot.Content.ReadFromJsonAsync<JsonElement>(Json);

        if (!skipPut)
        {
            using var raw = new HttpClient();
            var put = new ByteArrayContent(bytes);
            put.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            (await raw.PutAsync(s.GetProperty("uploadUrl").GetString(), put)).EnsureSuccessStatusCode();
        }
        return await customer.PostAsync($"/v1/client/session/uploads/{s.GetProperty("uploadedFileId").GetGuid()}/complete", null);
    }

    public async Task<JsonElement> WaitForAsync(string token, Func<JsonElement, bool> done, int timeoutSeconds = 30)
    {
        using var customer = Customer(token);
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            var state = await customer.GetFromJsonAsync<JsonElement>("/v1/client/session", Json);
            if (done(state)) return state;
            await Task.Delay(200);
        }
        throw new TimeoutException("Session did not reach the expected state.");
    }

    public static Func<JsonElement, bool> NextAction(params string[] actions) =>
        s => actions.Contains(s.GetProperty("nextAction").GetString());
}

[CollectionDefinition(Name)]
public sealed class KycCollection : ICollectionFixture<KycApiFixture>
{
    public const string Name = "kyc";
}
