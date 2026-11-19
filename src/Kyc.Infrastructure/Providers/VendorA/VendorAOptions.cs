namespace Kyc.Infrastructure.Providers.VendorA;

public sealed class VendorAOptions
{
    public const string Section = "Providers:VendorA";
    public const string Name = "vendor-a";

    /// <summary>Vendor A's API base URL and key. Unused by the sandbox client; a real HTTP client would need them.</summary>
    public string BaseUrl { get; set; } = "https://api.vendor-a.example/v3";
    public string ApiKey { get; set; } = "";

    /// <summary>Artificial latency in the sandbox client, so async behaviour and queue lag show up in demos.</summary>
    public int SandboxLatencyMs { get; set; } = 300;

    /// <summary>
    /// Decision thresholds calibrated for Vendor A's scores (normalised to 0–1). They belong to the vendor, not the
    /// decision engine: another vendor's 0.85 does not mean the same thing, so switching vendors means recalibrating.
    /// </summary>
    public VendorAThresholds Thresholds { get; set; } = new();
}

public sealed class VendorAThresholds
{
    public decimal FaceMatchApprove { get; set; } = 0.85m;
    public decimal FaceMatchReject { get; set; } = 0.60m;
    public decimal LivenessApprove { get; set; } = 0.80m;
}
