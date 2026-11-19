using System.Text.Json.Serialization;

namespace Kyc.Infrastructure.Providers.VendorA;

// Vendor A's own wire format, as its API documents it. Nothing outside this folder uses these types:
// VendorAAdapter translates them into our vendor-neutral contracts (ProviderResult / DocumentResult).

/// <summary>Vendor A's overall verdict on a check.</summary>
public static class VendorAResult
{
    public const string Clear = "clear";               // passed
    public const string Consider = "consider";         // needs a human look
    public const string Unidentified = "unidentified"; // image could not be processed
    public const string Rejected = "rejected";         // definitive fail
}

public sealed record VendorAReport(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("result")] string Result,
    /// <summary>0–100. Absent when the image could not be processed.</summary>
    [property: JsonPropertyName("score")] int? Score,
    /// <summary>Vendor A's sub-check names that raised a concern, e.g. "image_quality", "visual_authenticity".</summary>
    [property: JsonPropertyName("breakdown")] IReadOnlyList<string> Breakdown,
    /// <summary>Data read from the document (document checks only).</summary>
    [property: JsonPropertyName("properties")] VendorADocumentProperties? Properties = null);

public sealed record VendorADocumentProperties(
    [property: JsonPropertyName("document_number")] string DocumentNumber,
    [property: JsonPropertyName("document_type")] string DocumentType,
    [property: JsonPropertyName("first_name")] string FirstName,
    [property: JsonPropertyName("last_name")] string LastName,
    [property: JsonPropertyName("date_of_birth")] string DateOfBirth,
    [property: JsonPropertyName("date_of_expiry")] string? DateOfExpiry,
    [property: JsonPropertyName("issuing_country")] string IssuingCountry);

/// <summary>A non-2xx response from Vendor A's API.</summary>
public sealed class VendorAApiException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
