using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kyc.Domain;
using Kyc.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Kyc.IntegrationTests;

[Collection(KycCollection.Name)]
public sealed class VerificationJourneyTests(KycApiFixture fx)
{
    private static readonly JsonSerializerOptions Json = KycApiFixture.Json;

    [Fact]
    public async Task Full_journey_with_device_handoff_is_approved_and_audited()
    {
        var (sessionId, desktopToken) = await fx.CreateSessionAsync();

        // Desktop -> QR -> phone
        using var desktop = fx.Customer(desktopToken);
        var handoff = await (await desktop.PostAsync("/v1/client/session/handoff", null)).Content.ReadFromJsonAsync<JsonElement>(Json);
        var qrToken = handoff.GetProperty("handoffUrl").GetString()!.Split("#token=")[1];

        using var anon = fx.Anonymous();
        var redeem = await anon.PostAsJsonAsync("/v1/handoff/redeem", new { token = qrToken });
        redeem.EnsureSuccessStatusCode();
        var phoneToken = (await redeem.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("clientToken").GetString()!;

        // Phone uploads document, then selfie
        Assert.Equal(HttpStatusCode.Accepted, (await fx.UploadAsync(phoneToken, "IdentityDocument", KycApiFixture.Image())).StatusCode);
        await fx.WaitForAsync(phoneToken, KycApiFixture.NextAction("UPLOAD_SELFIE"));
        Assert.Equal(HttpStatusCode.Accepted, (await fx.UploadAsync(phoneToken, "Selfie", KycApiFixture.Image())).StatusCode);
        var final = await fx.WaitForAsync(phoneToken, KycApiFixture.NextAction("COMPLETE"));
        Assert.Equal("Approved", final.GetProperty("status").GetString());

        // The desktop sees the same progress
        var desktopView = await desktop.GetFromJsonAsync<JsonElement>("/v1/client/session", Json);
        Assert.Equal("Approved", desktopView.GetProperty("status").GetString());

        // Partner view: three checks and one automated decision
        using var partner = fx.Partner();
        var details = await partner.GetFromJsonAsync<JsonElement>($"/v1/verification-sessions/{sessionId}", Json);
        Assert.Equal(3, details.GetProperty("checks").GetArrayLength());
        var decision = details.GetProperty("decisions")[0];
        Assert.Equal("Approved", decision.GetProperty("outcome").GetString());
        Assert.Equal("System", decision.GetProperty("decidedBy").GetString());

        // Audit shows start, steps, decision, and whether a human stepped in
        var events = await partner.GetFromJsonAsync<JsonElement[]>($"/v1/verification-sessions/{sessionId}/events", Json);
        var types = events!.Select(e => e.GetProperty("eventType").GetString()).ToList();
        Assert.Equal("session.created", types[0]);
        Assert.Contains("handoff.redeemed", types);
        Assert.Contains("check.completed", types);
        Assert.Contains("decision.made", types);
        Assert.DoesNotContain(events!, e => e.GetProperty("actorType").GetString() == "Reviewer");
    }

    [Fact]
    public async Task Handoff_token_can_be_redeemed_exactly_once_even_under_concurrency()
    {
        var (_, token) = await fx.CreateSessionAsync();
        using var desktop = fx.Customer(token);
        var handoff = await (await desktop.PostAsync("/v1/client/session/handoff", null)).Content.ReadFromJsonAsync<JsonElement>(Json);
        var qrToken = handoff.GetProperty("handoffUrl").GetString()!.Split("#token=")[1];

        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            using var anon = fx.Anonymous();
            return (await anon.PostAsJsonAsync("/v1/handoff/redeem", new { token = qrToken })).StatusCode;
        }));

        Assert.Single(attempts, s => s == HttpStatusCode.OK);
        Assert.All(attempts.Where(s => s != HttpStatusCode.OK), s => Assert.Equal(HttpStatusCode.UnprocessableEntity, s));
    }

    [Fact]
    public async Task Issuing_a_new_qr_revokes_the_previous_one()
    {
        var (_, token) = await fx.CreateSessionAsync();
        using var desktop = fx.Customer(token);
        var first = (await (await desktop.PostAsync("/v1/client/session/handoff", null)).Content.ReadFromJsonAsync<JsonElement>(Json))
            .GetProperty("handoffUrl").GetString()!.Split("#token=")[1];
        await desktop.PostAsync("/v1/client/session/handoff", null);

        using var anon = fx.Anonymous();
        var res = await anon.PostAsJsonAsync("/v1/handoff/redeem", new { token = first });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
    }

    [Fact]
    public async Task Partners_cannot_see_each_others_sessions_and_customers_need_a_token()
    {
        var (sessionId, _) = await fx.CreateSessionAsync();

        using var otherPartner = fx.Partner(KycApiFixture.OtherPartnerKey);
        Assert.Equal(HttpStatusCode.NotFound, (await otherPartner.GetAsync($"/v1/verification-sessions/{sessionId}")).StatusCode);

        using var anon = fx.Anonymous();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/v1/client/session")).StatusCode);

        using var forged = fx.Customer("not-a-real-token-but-long-enough-xx");
        Assert.Equal(HttpStatusCode.Unauthorized, (await forged.GetAsync("/v1/client/session")).StatusCode);

        // Needs a named reviewer, and can't open another partner's session
        using var partner = fx.Partner();
        Assert.Equal(HttpStatusCode.BadRequest, (await partner.GetAsync("/v1/reviews")).StatusCode);
        using var otherReviewer = fx.Partner(KycApiFixture.OtherPartnerKey);
        otherReviewer.DefaultRequestHeaders.Add("X-Reviewer-Id", "staff-9");
        Assert.Equal(HttpStatusCode.NotFound, (await otherReviewer.GetAsync($"/v1/reviews/{sessionId}")).StatusCode);
    }

    [Fact]
    public async Task Idempotent_create_returns_same_session_and_rejects_a_different_body()
    {
        var key = Guid.NewGuid().ToString("N");
        using var partner = fx.Partner();

        async Task<HttpResponseMessage> Create(string reference)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/verification-sessions")
            {
                Content = JsonContent.Create(new { applicantReference = reference, channel = "Web" }),
            };
            req.Headers.Add("Idempotency-Key", key);
            return await partner.SendAsync(req);
        }

        var first = await Create("app-1");
        var second = await Create("app-1");
        var conflicting = await Create("app-2");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(
            (await first.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("sessionId").GetGuid(),
            (await second.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("sessionId").GetGuid());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, conflicting.StatusCode);
        Assert.Contains("idempotency_key_reused", await conflicting.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Completing_an_upload_that_never_happened_is_rejected()
    {
        var (_, token) = await fx.CreateSessionAsync();
        var res = await fx.UploadAsync(token, "IdentityDocument", KycApiFixture.Image(), skipPut: true);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Contains("upload_not_found", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Selfie_before_document_is_a_conflict()
    {
        var (_, token) = await fx.CreateSessionAsync();
        using var customer = fx.Customer(token);
        var res = await customer.PostAsJsonAsync("/v1/client/session/artifacts", new
        {
            type = "Selfie",
            contentType = "image/jpeg",
            sizeBytes = 10,
            sha256 = new string('a', 64),
        });
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains("unexpected_artifact", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Low_confidence_face_match_goes_to_manual_review_and_reviewer_decision_is_recorded()
    {
        var (sessionId, token) = await fx.CreateSessionAsync();
        await fx.UploadAsync(token, "IdentityDocument", KycApiFixture.Image());
        await fx.WaitForAsync(token, KycApiFixture.NextAction("UPLOAD_SELFIE"));
        await fx.UploadAsync(token, "Selfie", KycApiFixture.Image("FACE_WEAK"));
        await fx.WaitForAsync(token, s => s.GetProperty("status").GetString() == "ManualReview");

        using var reviewer = fx.Reviewer();
        var queue = await reviewer.GetFromJsonAsync<JsonElement[]>("/v1/reviews?limit=200", Json);
        Assert.Contains(queue!, q => q.GetProperty("sessionId").GetGuid() == sessionId);

        var @case = await reviewer.GetFromJsonAsync<JsonElement>($"/v1/reviews/{sessionId}", Json);
        Assert.Equal(2, @case.GetProperty("images").GetArrayLength());
        Assert.StartsWith("*", @case.GetProperty("identity").GetProperty("documentNumberMasked").GetString());

        var decide = await reviewer.PostAsJsonAsync($"/v1/reviews/{sessionId}/decision",
            new { outcome = "Rejected", reasonCode = "face_not_matching", notes = "Different person" });
        decide.EnsureSuccessStatusCode();

        // Second decision is refused since the session left review
        var again = await reviewer.PostAsJsonAsync($"/v1/reviews/{sessionId}/decision",
            new { outcome = "Approved", reasonCode = "X" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        using var partner = fx.Partner();
        var details = await partner.GetFromJsonAsync<JsonElement>($"/v1/verification-sessions/{sessionId}", Json);
        Assert.Equal("Rejected", details.GetProperty("status").GetString());
        var decisions = details.GetProperty("decisions").EnumerateArray().ToList();
        Assert.Equal(["ManualReview", "Rejected"], decisions.Select(d => d.GetProperty("outcome").GetString()));
        Assert.Equal(KycApiFixture.ReviewerId, decisions[1].GetProperty("reviewerId").GetString());

        // Audit names the reviewer and the partner they acted for
        var events = await partner.GetFromJsonAsync<JsonElement[]>($"/v1/verification-sessions/{sessionId}/events", Json);
        Assert.Contains(events!, e => e.GetProperty("eventType").GetString() == "decision.made"
            && e.GetProperty("actorId").GetString() == $"reviewer:{KycApiFixture.ReviewerId} via partner:partner-a");
    }

    [Fact]
    public async Task Unreadable_document_asks_for_recapture_then_continues()
    {
        var (_, token) = await fx.CreateSessionAsync();
        await fx.UploadAsync(token, "IdentityDocument", KycApiFixture.Image("DOC_BLURRY"));
        var state = await fx.WaitForAsync(token, s => s.GetProperty("recaptureRequested").GetBoolean());
        Assert.Equal("UPLOAD_DOCUMENT", state.GetProperty("nextAction").GetString());
        Assert.Equal(2, state.GetProperty("documentAttemptsRemaining").GetInt32());

        await fx.UploadAsync(token, "IdentityDocument", KycApiFixture.Image());
        await fx.WaitForAsync(token, KycApiFixture.NextAction("UPLOAD_SELFIE"));
    }

    [Fact]
    public async Task Transient_provider_failure_is_retried_durably()
    {
        var (sessionId, token) = await fx.CreateSessionAsync();
        await fx.UploadAsync(token, "IdentityDocument", KycApiFixture.Image("PROVIDER_FLAKY"));
        await fx.WaitForAsync(token, KycApiFixture.NextAction("UPLOAD_SELFIE"));

        using var partner = fx.Partner();
        var details = await partner.GetFromJsonAsync<JsonElement>($"/v1/verification-sessions/{sessionId}", Json);
        var check = details.GetProperty("checks")[0];
        Assert.Equal("Passed", check.GetProperty("outcome").GetString());
        Assert.Equal(2, check.GetProperty("attempt").GetInt32());
    }

    [Fact]
    public async Task Permanent_provider_failure_is_not_retried_and_goes_to_manual_review()
    {
        var (sessionId, token) = await fx.CreateSessionAsync();
        await fx.UploadAsync(token, "IdentityDocument", KycApiFixture.Image("PROVIDER_MISCONFIGURED"));
        await fx.WaitForAsync(token, s => s.GetProperty("status").GetString() == "ManualReview", timeoutSeconds: 10);

        using var scope = fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KycDbContext>();
        var job = await db.WorkflowJobs.SingleAsync(j => j.SessionId == sessionId);
        Assert.Equal(JobStatus.DeadLettered, job.Status);
        Assert.Equal(1, job.Attempts);
    }

    [Fact]
    public async Task Audit_events_are_append_only_at_the_database_level()
    {
        await fx.CreateSessionAsync();
        using var scope = fx.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KycDbContext>();
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync("UPDATE audit_events SET event_type = 'tampered'"));
        Assert.Contains("append-only", ex.MessageText);
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM audit_events"));
    }

    [Fact]
    public async Task Customer_view_does_not_leak_reason_codes()
    {
        var (_, token) = await fx.CreateSessionAsync();
        await fx.UploadAsync(token, "IdentityDocument", KycApiFixture.Image("DOC_TAMPERED"));
        var state = await fx.WaitForAsync(token, KycApiFixture.NextAction("COMPLETE"));
        Assert.Equal("Rejected", state.GetProperty("status").GetString());
        var raw = state.GetRawText();
        Assert.DoesNotContain("TAMPER", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reason", raw, StringComparison.OrdinalIgnoreCase);
    }
}
