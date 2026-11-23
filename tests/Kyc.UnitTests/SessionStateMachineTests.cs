using Kyc.Domain;

namespace Kyc.UnitTests;

public class SessionStateMachineTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 10, 0, 0, TimeSpan.Zero);

    private static VerificationSession NewSession(TimeSpan? lifetime = null) =>
        VerificationSession.Create("client", "app-123", Channel.Web, "idem-1", "hash", lifetime ?? TimeSpan.FromHours(1),
            Now, "10.0.0.1", "ua", "fp");

    [Theory]
    [InlineData(SessionStatus.Created, SessionStatus.DocumentSubmitted)]
    [InlineData(SessionStatus.DocumentSubmitted, SessionStatus.AwaitingSelfie)]
    [InlineData(SessionStatus.DocumentSubmitted, SessionStatus.Created)]
    [InlineData(SessionStatus.SelfieSubmitted, SessionStatus.PendingDecision)]
    [InlineData(SessionStatus.PendingDecision, SessionStatus.ManualReview)]
    [InlineData(SessionStatus.ManualReview, SessionStatus.Approved)]
    public void Allows_expected_transitions(SessionStatus from, SessionStatus to) =>
        Assert.True(SessionStateMachine.CanTransition(from, to));

    [Theory]
    [InlineData(SessionStatus.Created, SessionStatus.Approved)]          // can't skip the checks
    [InlineData(SessionStatus.Created, SessionStatus.SelfieSubmitted)]   // selfie before document
    [InlineData(SessionStatus.Approved, SessionStatus.Rejected)]         // terminal states are final
    [InlineData(SessionStatus.Rejected, SessionStatus.ManualReview)]
    [InlineData(SessionStatus.DocumentSubmitted, SessionStatus.Expired)] // never expire while we're processing
    [InlineData(SessionStatus.ManualReview, SessionStatus.Expired)]
    public void Rejects_illegal_transitions(SessionStatus from, SessionStatus to) =>
        Assert.False(SessionStateMachine.CanTransition(from, to));

    [Fact]
    public void Terminal_states_have_no_outgoing_transitions()
    {
        foreach (var s in Enum.GetValues<SessionStatus>().Where(SessionStateMachine.IsTerminal))
            Assert.Empty(SessionStateMachine.AllowedTargets(s));
    }

    [Fact]
    public void Every_status_is_covered_by_the_transition_table()
    {
        foreach (var s in Enum.GetValues<SessionStatus>())
            Assert.NotNull(SessionStateMachine.AllowedTargets(s));
    }

    [Fact]
    public void Creating_a_session_records_an_audit_event()
    {
        var session = NewSession();
        Assert.Equal(SessionStatus.Created, session.Status);
        Assert.Single(session.PendingEvents, e => e.EventType == AuditEventTypes.SessionCreated);
    }

    [Fact]
    public void Transition_records_a_published_status_change_event()
    {
        var session = NewSession();
        var uploadedFile = session.AddUploadedFile(UploadedFileType.IdentityDocument, "image/jpeg", 100, new string('a', 64), Guid.NewGuid(), Now);
        uploadedFile.MarkUploaded(Now);
        session.SubmitUploadedFile(uploadedFile, Now);

        Assert.Equal(SessionStatus.DocumentSubmitted, session.Status);
        Assert.Equal(1, session.DocumentAttempts);
        Assert.Contains(session.PendingEvents, e => e.EventType == AuditEventTypes.StatusChanged);
    }

    [Fact]
    public void Illegal_transition_throws_and_leaves_state_unchanged()
    {
        var session = NewSession();
        Assert.Throws<InvalidStateTransitionException>(() =>
            session.TransitionTo(SessionStatus.Approved, Now, "test"));
        Assert.Equal(SessionStatus.Created, session.Status);
    }

    [Fact]
    public void Selfie_upload_is_refused_before_document_is_accepted()
    {
        var session = NewSession();
        var ex = Assert.Throws<SessionStateConflictException>(() =>
            session.AddUploadedFile(UploadedFileType.Selfie, "image/jpeg", 100, new string('a', 64), Guid.NewGuid(), Now));
        Assert.Equal("unexpected_upload", ex.Code);
    }

    [Fact]
    public void New_upload_supersedes_an_unfinished_one_of_the_same_type()
    {
        var session = NewSession();
        var first = session.AddUploadedFile(UploadedFileType.IdentityDocument, "image/jpeg", 100, new string('a', 64), Guid.NewGuid(), Now);
        var second = session.AddUploadedFile(UploadedFileType.IdentityDocument, "image/jpeg", 100, new string('b', 64), Guid.NewGuid(), Now);
        Assert.Equal(UploadedFileStatus.Superseded, first.Status);
        Assert.Equal(UploadedFileStatus.PendingUpload, second.Status);
    }

    [Fact]
    public void Session_lapses_only_while_waiting_on_the_customer()
    {
        var session = NewSession(TimeSpan.FromMinutes(5));
        Assert.True(session.HasLapsed(Now.AddMinutes(6)));
        Assert.Throws<SessionExpiredException>(() => session.EnsureUsableByCustomer(Now.AddMinutes(6)));

        var uploadedFile = session.AddUploadedFile(UploadedFileType.IdentityDocument, "image/jpeg", 100, new string('a', 64), Guid.NewGuid(), Now);
        uploadedFile.MarkUploaded(Now);
        session.SubmitUploadedFile(uploadedFile, Now); // now DocumentSubmitted (processing)
        Assert.False(session.HasLapsed(Now.AddMinutes(6)));
    }

    [Fact]
    public void Decision_moves_session_to_matching_status_and_sets_completion()
    {
        var session = NewSession();
        Drive(session, SessionStatus.PendingDecision);
        session.ApplyDecision(Decision.Automated(session.Id, DecisionOutcome.Approved, ["ALL_CHECKS_PASSED"], "v1", 0, Now), Now);
        Assert.Equal(SessionStatus.Approved, session.Status);
        Assert.Equal(Now, session.CompletedAt);
        Assert.Contains(session.PendingEvents, e => e.EventType == AuditEventTypes.DecisionMade);
    }

    [Fact]
    public void Reviewer_cannot_decide_manual_review()
    {
        var ex = Assert.Throws<BusinessRuleException>(() =>
            Decision.Manual(Guid.NewGuid(), DecisionOutcome.ManualReview, "rev", "X", null, Now));
        Assert.Equal("invalid_outcome", ex.Code);
    }

    [Fact]
    public void Document_attempts_are_capped()
    {
        var session = NewSession();
        for (var i = 0; i < VerificationSession.MaxDocumentAttempts; i++)
        {
            var a = session.AddUploadedFile(UploadedFileType.IdentityDocument, "image/jpeg", 100, new string('a', 64), Guid.NewGuid(), Now);
            a.MarkUploaded(Now);
            session.SubmitUploadedFile(a, Now);
            session.TransitionTo(SessionStatus.Created, Now, "recapture");
        }
        var last = session.AddUploadedFile(UploadedFileType.IdentityDocument, "image/jpeg", 100, new string('a', 64), Guid.NewGuid(), Now);
        last.MarkUploaded(Now);
        var ex = Assert.Throws<SessionStateConflictException>(() => session.SubmitUploadedFile(last, Now));
        Assert.Equal("attempts_exhausted", ex.Code);
    }

    private static void Drive(VerificationSession s, SessionStatus target)
    {
        var path = new[] { SessionStatus.DocumentSubmitted, SessionStatus.AwaitingSelfie, SessionStatus.SelfieSubmitted, SessionStatus.PendingDecision };
        foreach (var step in path)
        {
            s.TransitionTo(step, Now, "test");
            if (step == target) return;
        }
    }
}
