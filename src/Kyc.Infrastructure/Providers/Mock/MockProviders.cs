using System.Security.Cryptography;
using System.Text;
using Kyc.Domain;
using Kyc.Infrastructure.Storage;
using Microsoft.Extensions.Options;

namespace Kyc.Infrastructure.Providers.Mock;

public sealed class MockProviderOptions
{
    public const string Section = "Providers:Mock";
    /// <summary>Artificial latency so async behaviour and queue lag show up in demos.</summary>
    public int LatencyMs { get; set; } = 300;
}

/// <summary>
/// Pulls scenario markers out of the uploaded bytes, the way vendor sandboxes use special test images.
/// A file containing <c>KYC_MOCK:FACE_WEAK</c> produces a low-confidence face match, and so on.
/// See README "Mock scenarios" for the full list.
/// </summary>
public static class MockMarkers
{
    public const string Prefix = "KYC_MOCK:";

    public static async Task<(HashSet<string> Markers, Dictionary<string, string> Values, string Sha256)> ReadAsync(
        IObjectStorage storage, string key, CancellationToken ct)
    {
        await using var stream = await storage.OpenReadAsync(key, ct);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();
        var (markers, values) = Parse(Encoding.ASCII.GetString(bytes));
        return (markers, values, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

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

    /// <summary>Simulated vendor faults: FLAKY fails the first attempt only, DOWN fails every attempt.</summary>
    public static void ThrowIfFaulted(HashSet<string> markers, int attempt)
    {
        if (markers.Contains("PROVIDER_DOWN"))
            throw new TransientProviderException("Mock provider unavailable (PROVIDER_DOWN).");
        if (markers.Contains("PROVIDER_FLAKY") && attempt <= 1)
            throw new TransientProviderException("Mock provider timed out (PROVIDER_FLAKY, attempt 1).");
    }

    public static string Reference(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..20];
}

public sealed class MockDocumentProvider(IObjectStorage storage, IOptions<MockProviderOptions> options, TimeProvider clock)
    : IDocumentVerificationProvider
{
    public string Name => "mock-docs";

    public async Task<DocumentResult> VerifyAsync(ProviderRequest request, CancellationToken ct)
    {
        await Task.Delay(options.Value.LatencyMs, ct);
        var (m, values, sha) = await MockMarkers.ReadAsync(storage, request.StorageKey, ct);
        MockMarkers.ThrowIfFaulted(m, request.Attempt);
        var reference = MockMarkers.Reference("doc");

        if (m.Contains("DOC_BLURRY"))
            return new DocumentResult(CheckOutcome.Unreadable, null, ["IMAGE_TOO_BLURRY"], reference, null);

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var identity = new ExtractedIdentity(
            // The same file always yields the same document number, so repeat-attempt detection can be demoed.
            DocumentNumber: values.GetValueOrDefault("DOC_NUMBER") ?? ("ID" + sha[..11].ToUpperInvariant()),
            DocumentType: "NATIONAL_ID",
            FullName: "Test Applicant",
            DateOfBirth: new DateOnly(1990, 1, 15),
            ExpiryDate: m.Contains("DOC_EXPIRED") ? today.AddYears(-1) : today.AddYears(5),
            IssuingCountry: "ZAF");

        // Like real vendors, still return the extracted data (MRZ etc.) when tampering is detected. Those are the
        // documents we most need to link to later attempts.
        if (m.Contains("DOC_TAMPERED"))
            return new DocumentResult(CheckOutcome.Failed, 0.12m, ["TAMPERING_DETECTED"], reference, identity);

        return m.Contains("DOC_UNCERTAIN")
            ? new DocumentResult(CheckOutcome.Inconclusive, 0.55m, ["FONT_ANOMALY_LOW_CONFIDENCE"], reference, identity)
            : new DocumentResult(CheckOutcome.Passed, 0.98m, [], reference, identity);
    }
}

public sealed class MockLivenessProvider(IObjectStorage storage, IOptions<MockProviderOptions> options) : ILivenessProvider
{
    public string Name => "mock-liveness";

    public async Task<ProviderResult> CheckAsync(ProviderRequest request, CancellationToken ct)
    {
        await Task.Delay(options.Value.LatencyMs, ct);
        var (m, _, _) = await MockMarkers.ReadAsync(storage, request.StorageKey, ct);
        MockMarkers.ThrowIfFaulted(m, request.Attempt);
        var reference = MockMarkers.Reference("liv");

        if (m.Contains("LIVENESS_SPOOF"))
            return new ProviderResult(CheckOutcome.Failed, 0.05m, ["PRESENTATION_ATTACK_SCREEN_REPLAY"], reference);
        if (m.Contains("LIVENESS_BLURRY"))
            return new ProviderResult(CheckOutcome.Unreadable, null, ["FACE_NOT_DETECTED"], reference);
        if (m.Contains("LIVENESS_UNCERTAIN"))
            return new ProviderResult(CheckOutcome.Inconclusive, 0.62m, ["LOW_CONFIDENCE"], reference);
        return new ProviderResult(CheckOutcome.Passed, 0.96m, [], reference);
    }
}

public sealed class MockFaceMatchProvider(IObjectStorage storage, IOptions<MockProviderOptions> options) : IFaceMatchProvider
{
    public string Name => "mock-facematch";

    public async Task<ProviderResult> MatchAsync(FaceMatchRequest request, CancellationToken ct)
    {
        await Task.Delay(options.Value.LatencyMs, ct);
        var (m, _, _) = await MockMarkers.ReadAsync(storage, request.SelfieStorageKey, ct);
        MockMarkers.ThrowIfFaulted(m, request.Attempt);
        var reference = MockMarkers.Reference("fm");

        if (m.Contains("FACE_MISMATCH"))
            return new ProviderResult(CheckOutcome.Failed, 0.21m, ["FACES_DO_NOT_MATCH"], reference);
        if (m.Contains("FACE_WEAK"))
            return new ProviderResult(CheckOutcome.Inconclusive, 0.72m, ["LOW_SIMILARITY"], reference);
        return new ProviderResult(CheckOutcome.Passed, 0.97m, [], reference);
    }
}
