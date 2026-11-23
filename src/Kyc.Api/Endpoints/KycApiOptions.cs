namespace Kyc.Api.Endpoints;

public sealed class KycApiOptions
{
    public const string Section = "Kyc";
    public int SessionLifetimeMinutes { get; set; } = 24 * 60;
    public int HandoffTtlSeconds { get; set; } = 300;
    // Front-end page that reads the token from the URL fragment and calls /v1/handoff/redeem.
    public string HandoffBaseUrl { get; set; } = "http://localhost:3000/verify/continue";
}
