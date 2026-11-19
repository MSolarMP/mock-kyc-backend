using System.Globalization;
using Kyc.Domain;

namespace Kyc.Infrastructure.Providers.VendorA;

// Maps Vendor A's verdicts, 0-100 scores, sub-checks and HTTP errors onto our vendor-neutral types.
public sealed class VendorAAdapter(IVendorAClient client)
    : IDocumentVerificationProvider, ILivenessProvider, IFaceMatchProvider
{
    public string Name => VendorAOptions.Name;

    public async Task<DocumentResult> VerifyAsync(ProviderRequest request, CancellationToken ct)
    {
        var report = await Call(() => client.CreateDocumentReportAsync(
            new VendorAImage(request.ImageUrl, request.StorageKey), request.IdempotencyKey, request.Attempt, ct));
        return new DocumentResult(MapOutcome(report.Result), MapScore(report.Score), MapReasons(report.Breakdown),
            report.Id, MapIdentity(report.Properties));
    }

    public async Task<ProviderResult> CheckAsync(ProviderRequest request, CancellationToken ct)
    {
        var report = await Call(() => client.CreateLivenessReportAsync(
            new VendorAImage(request.ImageUrl, request.StorageKey), request.IdempotencyKey, request.Attempt, ct));
        return ToResult(report);
    }

    public async Task<ProviderResult> MatchAsync(FaceMatchRequest request, CancellationToken ct)
    {
        var report = await Call(() => client.CreateFaceSimilarityReportAsync(
            new VendorAImage(request.SelfieUrl, request.SelfieStorageKey),
            new VendorAImage(request.DocumentUrl, request.DocumentStorageKey),
            request.IdempotencyKey, request.Attempt, ct));
        return ToResult(report);
    }

    public static CheckOutcome MapOutcome(string result) => result switch
    {
        VendorAResult.Clear => CheckOutcome.Passed,
        VendorAResult.Consider => CheckOutcome.Inconclusive,
        VendorAResult.Unidentified => CheckOutcome.Unreadable,
        VendorAResult.Rejected => CheckOutcome.Failed,
        // Unknown verdicts go to a human, never a pass.
        _ => CheckOutcome.Inconclusive,
    };

    public static decimal? MapScore(int? score) => score is { } s ? Math.Clamp(s, 0, 100) / 100m : null;

    private static readonly Dictionary<string, string> ReasonMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image_quality"] = "IMAGE_TOO_BLURRY",
        ["visual_authenticity"] = "TAMPERING_DETECTED",
        ["data_consistency"] = "DOCUMENT_DATA_INCONSISTENT",
        ["spoofing_detected"] = "PRESENTATION_ATTACK",
        ["face_not_detected"] = "FACE_NOT_DETECTED",
        ["low_confidence"] = "LOW_CONFIDENCE",
        ["face_comparison"] = "FACE_SIMILARITY_LOW",
    };

    // Unknown sub-checks are kept with a prefix rather than dropped.
    public static IReadOnlyList<string> MapReasons(IReadOnlyList<string> breakdown) =>
        breakdown.Select(b => ReasonMap.GetValueOrDefault(b) ?? $"VENDOR_A_{b.ToUpperInvariant()}").ToList();

    private static ExtractedIdentity? MapIdentity(VendorADocumentProperties? p) => p is null
        ? null
        : new ExtractedIdentity(
            p.DocumentNumber,
            p.DocumentType.ToUpperInvariant(),
            $"{p.FirstName} {p.LastName}".Trim(),
            DateOnly.ParseExact(p.DateOfBirth, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            p.DateOfExpiry is null ? null : DateOnly.ParseExact(p.DateOfExpiry, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            p.IssuingCountry);

    private static ProviderResult ToResult(VendorAReport r) =>
        new(MapOutcome(r.Result), MapScore(r.Score), MapReasons(r.Breakdown), r.Id);

    // 408/429/5xx are transient and retried. Other 4xx are permanent: retrying won't help.
    public static async Task<VendorAReport> Call(Func<Task<VendorAReport>> call)
    {
        try
        {
            return await call();
        }
        catch (VendorAApiException ex) when (ex.StatusCode is 408 or 429 or >= 500)
        {
            throw new TransientProviderException($"Vendor A returned {ex.StatusCode}.", ex);
        }
        catch (VendorAApiException ex)
        {
            throw new PermanentProviderException($"Vendor A returned {ex.StatusCode}.", ex);
        }
    }
}
