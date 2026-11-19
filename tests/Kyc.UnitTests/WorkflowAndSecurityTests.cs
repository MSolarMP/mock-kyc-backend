using System.Security.Cryptography;
using Kyc.Domain;
using Kyc.Domain.Workflow;
using Kyc.Infrastructure.Providers;
using Kyc.Infrastructure.Providers.VendorA;
using Kyc.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace Kyc.UnitTests;

public class WorkflowRulesTests
{
    [Theory]
    [InlineData(CheckOutcome.Passed, 1, NextStep.Continue)]
    [InlineData(CheckOutcome.Inconclusive, 1, NextStep.Continue)]          // a human decides later
    [InlineData(CheckOutcome.Unreadable, 1, NextStep.RequestRecapture)]    // the customer can fix it
    [InlineData(CheckOutcome.Unreadable, 3, NextStep.GoToDecision)]        // attempts used up
    [InlineData(CheckOutcome.Failed, 1, NextStep.GoToDecision)]            // stop spending vendor calls
    public void Document_routing(CheckOutcome outcome, int attempts, NextStep expected) =>
        Assert.Equal(expected, WorkflowRules.AfterDocumentCheck(outcome, attempts));

    [Fact]
    public void Retry_delay_grows_exponentially_and_is_capped()
    {
        var rng = new Random(42);
        var d1 = WorkflowRules.RetryDelay(1, rng);
        var d4 = WorkflowRules.RetryDelay(4, rng);
        var d20 = WorkflowRules.RetryDelay(20, rng);
        Assert.InRange(d1.TotalSeconds, 2, 2.4);
        Assert.InRange(d4.TotalSeconds, 16, 19.2);
        Assert.InRange(d20.TotalSeconds, 300, 360);
    }
}

public class SecurityTests
{
    private static IOptions<SecurityOptions> Options() => Microsoft.Extensions.Options.Options.Create(new SecurityOptions
    {
        FieldEncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        IdentifierHmacKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        KeyId = 1,
    });

    [Fact]
    public void Field_protector_round_trips_and_uses_random_nonces()
    {
        var protector = new FieldProtector(Options());
        var a = protector.Protect("{\"name\":\"Jane\"}");
        var b = protector.Protect("{\"name\":\"Jane\"}");
        Assert.NotEqual(a, b);
        Assert.Equal("{\"name\":\"Jane\"}", protector.Unprotect(a));
    }

    [Fact]
    public void Field_protector_detects_tampering()
    {
        var protector = new FieldProtector(Options());
        var payload = protector.Protect("secret");
        payload[^1] ^= 0xFF;
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(payload));
    }

    [Fact]
    public void Identifier_hash_is_normalised_and_keyed()
    {
        var options = Options();
        var hasher = new IdentifierHasher(options);
        Assert.Equal(hasher.Hash("9001 0150 0908 7"), hasher.Hash("9001015009087"));
        Assert.NotEqual(hasher.Hash("9001015009087"), new IdentifierHasher(Options()).Hash("9001015009087"));
    }

    [Fact]
    public void Tokens_are_url_safe_unique_and_hash_deterministically()
    {
        var t1 = Tokens.Generate();
        var t2 = Tokens.Generate();
        Assert.NotEqual(t1, t2);
        Assert.DoesNotContain('+', t1);
        Assert.DoesNotContain('/', t1);
        Assert.Equal(Tokens.Hash(t1), Tokens.Hash(t1));
        Assert.Equal(64, Tokens.Hash(t1).Length);
    }
}

public class VendorASandboxTests
{
    [Fact]
    public void Parses_flags_and_values()
    {
        var (markers, values) = SandboxMarkers.Parse("ÿØ junk KYC_MOCK:FACE_WEAK more KYC_MOCK:DOC_NUMBER=8001015009087 end");
        Assert.Contains("FACE_WEAK", markers);
        Assert.Equal("8001015009087", values["DOC_NUMBER"]);
    }

    [Fact]
    public void Flaky_vendor_fails_first_attempt_only()
    {
        var markers = new HashSet<string> { "PROVIDER_FLAKY" };
        Assert.Equal(504, Assert.Throws<VendorAApiException>(() => SandboxMarkers.ThrowIfFaulted(markers, 1)).StatusCode);
        SandboxMarkers.ThrowIfFaulted(markers, 2);
    }
}

/// <summary>The translation layer: Vendor A's vocabulary must map onto ours, and nothing unknown may become a pass.</summary>
public class VendorAAdapterTests
{
    [Theory]
    [InlineData("clear", CheckOutcome.Passed)]
    [InlineData("consider", CheckOutcome.Inconclusive)]
    [InlineData("unidentified", CheckOutcome.Unreadable)]
    [InlineData("rejected", CheckOutcome.Failed)]
    [InlineData("something_new", CheckOutcome.Inconclusive)]
    public void Maps_verdicts(string result, CheckOutcome expected) =>
        Assert.Equal(expected, VendorAAdapter.MapOutcome(result));

    [Fact]
    public void Normalises_scores_to_0_1()
    {
        Assert.Equal(0.72m, VendorAAdapter.MapScore(72));
        Assert.Equal(1m, VendorAAdapter.MapScore(140));
        Assert.Null(VendorAAdapter.MapScore(null));
    }

    [Fact]
    public void Maps_known_reasons_and_keeps_unknown_ones()
    {
        var reasons = VendorAAdapter.MapReasons(["visual_authenticity", "hologram_missing"]);
        Assert.Equal(["TAMPERING_DETECTED", "VENDOR_A_HOLOGRAM_MISSING"], reasons);
    }

    [Theory]
    [InlineData(503, typeof(TransientProviderException))]
    [InlineData(504, typeof(TransientProviderException))]
    [InlineData(429, typeof(TransientProviderException))]
    [InlineData(401, typeof(PermanentProviderException))]
    [InlineData(400, typeof(PermanentProviderException))]
    public async Task Classifies_http_errors(int status, Type expected)
    {
        var ex = await Record.ExceptionAsync(() =>
            VendorAAdapter.Call(() => throw new VendorAApiException(status, "boom")));
        Assert.IsType(expected, ex);
    }
}
