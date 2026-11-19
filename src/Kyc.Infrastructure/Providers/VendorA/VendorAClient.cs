using System.Security.Cryptography;
using System.Text;
using Kyc.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace Kyc.Infrastructure.Providers.VendorA;

/// <summary>An image Vendor A should check. Real Vendor A downloads <see cref="Url"/> itself (a short-lived pre-signed GET).</summary>
/// <param name="StorageKey">Only the sandbox uses this, to read the file directly instead of over the network.</param>
public sealed record VendorAImage(Uri Url, string StorageKey);

/// <summary>Vendor A's API, in Vendor A's terms. A production build adds an HttpClient implementation of this.</summary>
public interface IVendorAClient
{
    Task<VendorAReport> CreateDocumentReportAsync(VendorAImage document, string idempotencyKey, int attempt, CancellationToken ct);
    Task<VendorAReport> CreateLivenessReportAsync(VendorAImage selfie, string idempotencyKey, int attempt, CancellationToken ct);
    Task<VendorAReport> CreateFaceSimilarityReportAsync(VendorAImage selfie, VendorAImage document, string idempotencyKey,
        int attempt, CancellationToken ct);
}

/// <summary>
/// Stand-in for Vendor A's sandbox. Like real vendor sandboxes, it decides the outcome from test markers inside the
/// uploaded file (<c>KYC_MOCK:FACE_WEAK</c>, <c>KYC_MOCK:PROVIDER_DOWN</c>, ...; see README "Mock scenarios") and answers
/// in Vendor A's own format. No marker means a clean pass.
/// </summary>
public sealed class SandboxVendorAClient(IObjectStorage storage, IOptions<VendorAOptions> options, TimeProvider clock)
    : IVendorAClient
{
    public async Task<VendorAReport> CreateDocumentReportAsync(VendorAImage document, string idempotencyKey, int attempt,
        CancellationToken ct)
    {
        var (m, values, sha) = await ReadAsync(document, attempt, ct);
        var id = ReportId("doc");
        if (m.Contains("DOC_BLURRY"))
            return new VendorAReport(id, VendorAResult.Unidentified, null, ["image_quality"]);

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var properties = new VendorADocumentProperties(
            // The same file always yields the same document number, so repeat-attempt detection can be demoed.
            DocumentNumber: values.GetValueOrDefault("DOC_NUMBER") ?? ("ID" + sha[..11].ToUpperInvariant()),
            DocumentType: "national_identity_card",
            FirstName: "Test",
            LastName: "Applicant",
            DateOfBirth: "1990-01-15",
            DateOfExpiry: (m.Contains("DOC_EXPIRED") ? today.AddYears(-1) : today.AddYears(5)).ToString("yyyy-MM-dd"),
            IssuingCountry: "ZAF");

        // Like real vendors, still return the extracted data when tampering is detected: those are the documents we
        // most need to link to later attempts.
        if (m.Contains("DOC_TAMPERED"))
            return new VendorAReport(id, VendorAResult.Rejected, 12, ["visual_authenticity"], properties);
        if (m.Contains("DOC_UNCERTAIN"))
            return new VendorAReport(id, VendorAResult.Consider, 55, ["data_consistency"], properties);
        return new VendorAReport(id, VendorAResult.Clear, 98, [], properties);
    }

    public async Task<VendorAReport> CreateLivenessReportAsync(VendorAImage selfie, string idempotencyKey, int attempt,
        CancellationToken ct)
    {
        var (m, _, _) = await ReadAsync(selfie, attempt, ct);
        var id = ReportId("liv");
        if (m.Contains("LIVENESS_SPOOF")) return new VendorAReport(id, VendorAResult.Rejected, 5, ["spoofing_detected"]);
        if (m.Contains("LIVENESS_BLURRY")) return new VendorAReport(id, VendorAResult.Unidentified, null, ["face_not_detected"]);
        if (m.Contains("LIVENESS_UNCERTAIN")) return new VendorAReport(id, VendorAResult.Consider, 62, ["low_confidence"]);
        return new VendorAReport(id, VendorAResult.Clear, 96, []);
    }

    public async Task<VendorAReport> CreateFaceSimilarityReportAsync(VendorAImage selfie, VendorAImage document,
        string idempotencyKey, int attempt, CancellationToken ct)
    {
        var (m, _, _) = await ReadAsync(selfie, attempt, ct);
        var id = ReportId("fm");
        if (m.Contains("FACE_MISMATCH")) return new VendorAReport(id, VendorAResult.Rejected, 21, ["face_comparison"]);
        if (m.Contains("FACE_WEAK")) return new VendorAReport(id, VendorAResult.Consider, 72, ["face_comparison"]);
        return new VendorAReport(id, VendorAResult.Clear, 97, []);
    }

    private async Task<(HashSet<string> Markers, Dictionary<string, string> Values, string Sha256)> ReadAsync(
        VendorAImage image, int attempt, CancellationToken ct)
    {
        await Task.Delay(options.Value.SandboxLatencyMs, ct);
        await using var stream = await storage.OpenReadAsync(image.StorageKey, ct);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();
        var (markers, values) = SandboxMarkers.Parse(Encoding.ASCII.GetString(bytes));
        SandboxMarkers.ThrowIfFaulted(markers, attempt);
        return (markers, values, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    private static string ReportId(string prefix) => $"va_{prefix}_{Guid.NewGuid():N}"[..24];
}

/// <summary>Parses sandbox test markers (<c>KYC_MOCK:NAME</c> or <c>KYC_MOCK:NAME=value</c>) out of a file's bytes.</summary>
public static class SandboxMarkers
{
    public const string Prefix = "KYC_MOCK:";

    public static (HashSet<string> Markers, Dictionary<string, string> Values) Parse(string content)
    {
        var markers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        while ((index = content.IndexOf(Prefix, index, StringComparison.Ordinal)) >= 0)
        {
            index += Prefix.Length;
            var end = index;
            while (end < content.Length && (char.IsLetterOrDigit(content[end]) || content[end] is '_' or '=' or '-'))
                end++;
            var token = content[index..end];
            var eq = token.IndexOf('=');
            if (eq > 0) values[token[..eq]] = token[(eq + 1)..];
            else if (token.Length > 0) markers.Add(token);
            index = end;
        }
        return (markers, values);
    }

    /// <summary>
    /// Simulated API faults, as HTTP errors: PROVIDER_FLAKY times out on the first attempt only, PROVIDER_DOWN returns
    /// 503 every time, PROVIDER_MISCONFIGURED returns 401 (a permanent error: retrying can't help).
    /// </summary>
    public static void ThrowIfFaulted(HashSet<string> markers, int attempt)
    {
        if (markers.Contains("PROVIDER_MISCONFIGURED"))
            throw new VendorAApiException(401, "Vendor A rejected the API key (PROVIDER_MISCONFIGURED).");
        if (markers.Contains("PROVIDER_DOWN"))
            throw new VendorAApiException(503, "Vendor A unavailable (PROVIDER_DOWN).");
        if (markers.Contains("PROVIDER_FLAKY") && attempt <= 1)
            throw new VendorAApiException(504, "Vendor A gateway timeout (PROVIDER_FLAKY, attempt 1).");
    }
}
