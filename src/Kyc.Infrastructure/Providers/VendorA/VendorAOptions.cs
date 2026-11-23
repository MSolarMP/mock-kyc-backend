namespace Kyc.Infrastructure.Providers.VendorA;

public sealed class VendorAOptions
{
    public const string Section = "Providers:VendorA";
    public const string Name = "vendor-a";

    // Not used by the sandbox client.
    public string BaseUrl { get; set; } = "https://api.vendor-a.example/v3";
    public string ApiKey { get; set; } = "";

    // Fake latency so queue lag shows up in demos.
    public int SandboxLatencyMs { get; set; } = 300;

    // Thresholds live with the vendor.
    public VendorAThresholds Thresholds { get; set; } = new();
}

public sealed class VendorAThresholds
{
    public decimal FaceMatchApprove { get; set; } = 0.85m;
    public decimal FaceMatchReject { get; set; } = 0.60m;
    public decimal LivenessApprove { get; set; } = 0.80m;
}
