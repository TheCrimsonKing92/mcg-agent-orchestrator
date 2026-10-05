using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Parallel-safe: every fact owns its journal and decision store; no processes or git are invoked.
public sealed class ConductorDriverTestsOwnerReviewHold
{
    private const string Candidate = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherCandidate = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Fingerprint = "v1:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    [Fact]
    public void OwnerProtectedConfigurationOnlyFailureHoldsWithoutDeveloperRetry()
    {
        using var fixture = new Fixture(OwnerCheck());
        AssertOwnerHold(fixture);
    }

    [Fact]
    public void TrustedDimensionsOnlyFailureHoldsWithoutDeveloperRetry()
    {
        using var fixture = new Fixture(OwnerCheck("acceptance manifest trusted dimensions"));
        AssertOwnerHold(fixture);
    }

    private static void AssertOwnerHold(Fixture fixture)
    {
        var result = fixture.Advance();
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Equal(GoalLifecycleState.Verified, held.State);
        Assert.Equal(0, fixture.Retries);
        Assert.Equal(0, fixture.Goal.AutomaticAcceptanceRetryCount);
        Assert.All(fixture.Goal.Tasks, task =>
        {
            Assert.Equal(0, task.CriterionRetryCount);
            Assert.Equal(WorkTaskStatus.Completed, task.Status);
        });
        Assert.Equal(GoalStatus.Verified, fixture.Goal.Status);
        Assert.Equal(0, fixture.InboxEscalations);
        Assert.Equal(0, fixture.Dispatches);
        var text = Assert.Single(fixture.Escalations);
        Assert.Contains(Candidate, text, StringComparison.Ordinal);
        Assert.Contains($".\\mcg-orchestrator.cmd approve-policy-change {fixture.Goal.Id.Value[..8]} {Candidate} --text-file <reason-file>", text, StringComparison.Ordinal);
        Assert.Contains("changed field(s) engine.maxParallelShards", text, StringComparison.Ordinal);
        Assert.Contains(Fingerprint, text, StringComparison.Ordinal);
        Assert.Contains("cancel-goal", text, StringComparison.Ordinal);
        Assert.Contains("abandon-goal", text, StringComparison.Ordinal);
        Assert.Single(GoalOperationJournal.Read(fixture.Root, fixture.Goal.Id).Entries,
            entry => entry.Operation == GoalOperationJournal.OwnerReviewHoldOperation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApprovalForHeldCandidateRegatesWithoutWorkerRound(bool approveByFingerprint)
    {
        using var fixture = new Fixture(OwnerCheck());
        AssertOwnerHold(fixture);
        fixture.Driver = fixture.CreateDriver(); // Read the durable marker through a fresh driver.
        Assert.IsType<ConductorAdvanceOutcome.Held>(fixture.Advance().Outcome);
        Assert.Equal(1, fixture.Gates);
        Assert.Single(fixture.Escalations);

        fixture.Approve(approveByFingerprint ? OtherCandidate : Candidate,
            approveByFingerprint ? Fingerprint : null);
        fixture.Summary = new AcceptanceVerificationSummary(true, [], BranchHeadSha: Candidate);
        var result = fixture.Advance();

        Assert.IsNotType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Equal(new[] { Candidate, Candidate }, fixture.GatedCandidates);
        Assert.Equal(2, fixture.Gates);
        Assert.Equal(0, fixture.Dispatches);
        Assert.Equal(0, fixture.Retries);
        Assert.Equal(0, fixture.Goal.AutomaticAcceptanceRetryCount);
        Assert.All(fixture.Goal.Tasks, task => Assert.Equal(WorkTaskStatus.Completed, task.Status));
        Assert.Single(fixture.Escalations);
    }

    [Fact]
    public void UnrelatedApprovalRemainsHeldAcrossRestoredGoalAndFreshDriver()
    {
        using var fixture = new Fixture(OwnerCheck());
        AssertOwnerHold(fixture);
        fixture.Approve(OtherCandidate, "v1:" + new string('d', 64));
        var restored = AgentOrchestratorKernel.FromSnapshot(fixture.Kernel.ExportSnapshot()).GetGoal(fixture.Goal.Id);
        var driver = fixture.CreateDriver();

        Assert.IsType<ConductorAdvanceOutcome.Held>(driver.AdvanceOnce(restored, ConductorAutonomyPolicy.Conservative).Outcome);
        Assert.IsType<ConductorAdvanceOutcome.Held>(driver.AdvanceOnce(restored, ConductorAutonomyPolicy.Conservative).Outcome);
        Assert.Equal(1, fixture.Gates);
        Assert.Single(fixture.Escalations);
        Assert.Equal(0, fixture.Retries);
        Assert.Equal(0, fixture.Dispatches);
    }

    [Fact]
    public void BackgroundFailureRestoresVerifiedWithoutInvalidatingLanes()
    {
        using var fixture = new Fixture(OwnerCheck());
        fixture.Kernel.BeginGoalAcceptanceVerification(fixture.Goal.Id, "Background gate started.");
        fixture.Kernel.ReconcileGoalAcceptanceFailed(fixture.Goal.Id,
            ["owner-protected configuration"], "Background gate finished.", Candidate, "main");
        Assert.Equal(GoalStatus.AcceptanceFailed, fixture.Goal.Status);
        var candidate = ConductorParallelAcceptanceCandidate.Create(fixture.Goal, 0, ["config/acceptance-manifest.json"], Candidate, "main");

        var result = fixture.Driver.CompleteParallelLandingAcceptance(candidate,
            ConductorAutonomyPolicy.Conservative, fixture.Summary, out var leaseHeld);

        Assert.False(leaseHeld);
        Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Equal(GoalStatus.Verified, fixture.Goal.Status);
        fixture.Approve(Candidate);
        fixture.Summary = new AcceptanceVerificationSummary(true, [], BranchHeadSha: Candidate);
        fixture.Advance();
        Assert.Equal(1, fixture.Gates);
        Assert.Equal(0, fixture.Retries);
        Assert.Equal(0, fixture.Dispatches);
        Assert.All(fixture.Goal.Tasks, task => Assert.Equal(WorkTaskStatus.Completed, task.Status));
    }

    [Fact]
    public void OwnerProtectedWithFailingTestCheckReopensDeveloperAsToday()
    {
        using var fixture = new Fixture(OwnerCheck(), new AcceptanceCheckResult("infrastructure tests", false, 1, "assertion failed"));
        AssertDeveloperRetry(fixture);
    }

    [Fact]
    public void TrustedDimensionsComparisonUnavailableReopensDeveloperAsToday()
    {
        using var fixture = new Fixture(OwnerCheck("acceptance manifest trusted dimensions") with
            { ResultSummary = "trusted manifest comparison unavailable" });
        AssertDeveloperRetry(fixture);
    }

    [Fact]
    public void AdditionalNamedFailureWithoutDetailReopensDeveloperAsToday()
    {
        using var fixture = new Fixture(OwnerCheck());
        fixture.Summary = fixture.Summary with { FailedChecks = ["owner-protected configuration", "tests"] };
        AssertDeveloperRetry(fixture);
    }

    [Fact]
    public void NonOwnerAdvisoryFailureAlsoPreventsOwnerOnlyHold()
    {
        using var fixture = new Fixture(OwnerCheck(), new AcceptanceCheckResult("advisory review", false, 1, "review failed", Advisory: true));
        AssertDeveloperRetry(fixture);
    }

    [Fact]
    public void MissingCandidateShaReopensDeveloperAsToday()
    {
        using var fixture = new Fixture(OwnerCheck());
        fixture.CurrentCandidate = "";
        fixture.Summary = fixture.Summary with { BranchHeadSha = null };
        AssertDeveloperRetry(fixture);
    }

    [Fact]
    public void NonCheckFailureDoesNotEnterOwnerHold()
    {
        using var fixture = new Fixture(OwnerCheck());
        fixture.Summary = AcceptanceVerificationSummary.Failed;
        Assert.IsType<ConductorAdvanceOutcome.Escalated>(fixture.Advance().Outcome);
        Assert.Empty(fixture.Escalations);
        Assert.Equal(1, fixture.InboxEscalations);
        Assert.Equal(0, fixture.Retries);
    }

    [Fact]
    public void TrimmedCaseInsensitiveOwnerReviewCheckUsesDetailFallback()
    {
        using var fixture = new Fixture(OwnerCheck(" OWNER-PROTECTED CONFIGURATION ") with
            { ResultSummary = " OPERATOR REVIEW REQUIRED ", OutputTail = "guarded policy requires an owner decision" });
        Assert.IsType<ConductorAdvanceOutcome.Held>(fixture.Advance().Outcome);
        Assert.Contains("changed-protected-fields=guarded policy requires an owner decision",
            Assert.Single(fixture.Escalations), StringComparison.Ordinal);
        Assert.Equal(0, fixture.Retries);
    }

    [Fact]
    public void AlreadyApprovedCandidateDoesNotEnterAnotherHold()
    {
        using var fixture = new Fixture(OwnerCheck());
        fixture.Approve(Candidate);
        AssertDeveloperRetry(fixture);
    }

    private static void AssertDeveloperRetry(Fixture fixture)
    {
        Assert.IsType<ConductorAdvanceOutcome.Executed>(fixture.Advance().Outcome);
        Assert.Equal(1, fixture.Retries);
        Assert.Equal(1, fixture.Goal.AutomaticAcceptanceRetryCount);
        var task = Assert.Single(fixture.Goal.Tasks);
        Assert.Equal(1, task.CriterionRetryCount);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.StartsWith("Acceptance criteria unmet; retrying task with feedback (attempt 1/", fixture.RetryMessage!, StringComparison.Ordinal);
        Assert.Empty(fixture.Escalations);
    }

    [Fact]
    public void FingerprintUnavailableStillHoldsAndAcceptsShaApproval()
    {
        using var fixture = new Fixture(OwnerCheck());
        fixture.ResolveFingerprint = (_, _) => throw new IOException("Fingerprint source unavailable.");
        fixture.Driver = fixture.CreateDriver();
        Assert.IsType<ConductorAdvanceOutcome.Held>(fixture.Advance().Outcome);
        Assert.DoesNotContain("fingerprint=", Assert.Single(fixture.Escalations), StringComparison.Ordinal);
        fixture.Approve(Candidate);
        fixture.Summary = new AcceptanceVerificationSummary(true, [], BranchHeadSha: Candidate);
        fixture.Advance();
        Assert.Equal(2, fixture.Gates);
        Assert.Equal(0, fixture.Retries);
    }

    [Fact]
    public void HeldCandidateIsExcludedFromParallelAndCohortAdmissionUntilApproved()
    {
        using var fixture = new Fixture(OwnerCheck());
        AssertOwnerHold(fixture);
        Assert.Null(fixture.Driver.TryBuildParallelAcceptanceCandidate(fixture.Goal, ConductorAutonomyPolicy.Conservative, 0, out _));
        Assert.Equal(GateReadyCandidateExclusionReason.OwnerReviewHold,
            Assert.IsType<GateReadyCandidateProjectionResult.Excluded>(fixture.Driver.ProjectGateReadyCandidate(fixture.Goal, ConductorAutonomyPolicy.Conservative)).Reason);
        Assert.Equal(GateReadyCandidateExclusionReason.OwnerReviewHold,
            Assert.IsType<GateReadyCandidateProjectionResult.Excluded>(fixture.Driver.ProjectInReviewCohortPartner(fixture.Goal, ConductorAutonomyPolicy.Conservative)).Reason);
        fixture.Approve(Candidate);
        Assert.NotEqual(GateReadyCandidateExclusionReason.OwnerReviewHold,
            Assert.IsType<GateReadyCandidateProjectionResult.Excluded>(fixture.Driver.ProjectGateReadyCandidate(fixture.Goal, ConductorAutonomyPolicy.Conservative)).Reason);
    }

    [Fact]
    public void UnavailableCurrentHeadDoesNotReleaseUnapprovedHold()
    {
        using var fixture = new Fixture(OwnerCheck());
        AssertOwnerHold(fixture);
        fixture.CurrentCandidate = "";
        Assert.IsType<ConductorAdvanceOutcome.Held>(fixture.Advance().Outcome);
        Assert.Equal(1, fixture.Gates);
        Assert.Single(fixture.Escalations);
    }

    [Fact]
    public void ChangedCandidateIsEvaluatedAfreshAndRaisesNewOwnerQuestion()
    {
        using var fixture = new Fixture(OwnerCheck());
        AssertOwnerHold(fixture);
        fixture.CurrentCandidate = OtherCandidate;
        fixture.Summary = fixture.Summary with { BranchHeadSha = OtherCandidate };
        Assert.IsType<ConductorAdvanceOutcome.Held>(fixture.Advance().Outcome);
        Assert.Equal(2, fixture.Gates);
        Assert.Equal(2, fixture.Escalations.Count);
        Assert.Contains(OtherCandidate, fixture.Escalations[1], StringComparison.Ordinal);
        fixture.Advance();
        Assert.Equal(2, fixture.Gates);
        Assert.Equal(2, fixture.Escalations.Count);
    }

    private static AcceptanceCheckResult OwnerCheck(string name = "owner-protected configuration") =>
        new(name, false, 1, "config/acceptance-manifest.json: changed field(s) engine.maxParallelShards; owner decision required for this candidate",
            ResultSummary: "operator review required");

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = ConductorDriverTests.CreateTempDirectory();
        internal readonly AgentOrchestratorKernel Kernel;
        internal readonly Goal Goal;
        internal readonly ICollaborationItemStore Decisions;
        internal readonly List<string> Escalations = [];
        internal readonly List<string> GatedCandidates = [];
        internal ConductorDriver Driver;
        internal AcceptanceVerificationSummary Summary;
        internal string CurrentCandidate = Candidate;
        internal Func<Goal, string, string?> ResolveFingerprint = (_, _) => Fingerprint;
        internal int Gates, Retries, Dispatches, InboxEscalations;
        internal string? RetryMessage;

        internal Fixture(params AcceptanceCheckResult[] checks)
        {
            (Kernel, Goal) = SimpleGoal();
            PassVerification(Kernel, Goal, Goal.Tasks.Single());
            Decisions = CollaborationItemStore.ForDirectory(Path.Combine(Root, ".orchestrator"));
            Summary = new AcceptanceVerificationSummary(false, checks,
                FailedChecks: checks.Select(check => check.Name).ToArray(), BranchHeadSha: Candidate, MainHeadSha: "main");
            Driver = CreateDriver();
        }

        internal ConductorDriver CreateDriver()
        {
            var driver = MakeDriver(
                runAcceptanceSummary: _ => { Gates++; GatedCandidates.Add(CurrentCandidate); return Summary; },
                dispatchAndStart: _ => { Dispatches++; throw new InvalidOperationException("Owner review must not dispatch a worker."); },
                startRecordedDispatches: _ => { Dispatches++; throw new InvalidOperationException("Owner review must not start a worker."); },
                retryTaskWithCause: (goalId, taskId, message, round, cause) =>
                {
                    Retries++;
                    RetryMessage = message;
                    Assert.Equal(RetryCause.CriterionEvidenceOwnerMismatch, cause);
                    return Kernel.RetryTask(goalId, taskId, message, retryRoundKind: round, retryCause: cause);
                },
                recordCriterionRetryFeedback: Kernel.RecordCriterionRetryFeedback,
                recordAcceptanceFailure: (goal, checks, branch, main, attributions, baseline) =>
                    Kernel.RecordAcceptanceFailure(goal.Id, checks, branch, main, attributions, baseline),
                resolveAcceptanceHeads: _ => (CurrentCandidate, "main"),
                writeEscalation: (_, _, _) => InboxEscalations++, executionDirectory: Root);
            driver.OverrideOwnerReviewHoldForTests(Decisions, (_, text) => Escalations.Add(text), ResolveFingerprint, Kernel);
            return driver;
        }

        internal ConductorAdvanceResult Advance() => Driver.AdvanceOnce(Goal, ConductorAutonomyPolicy.Conservative);

        internal void Approve(string sha, string? fingerprint = null)
        {
            var now = DateTimeOffset.Parse("2026-10-01T12:00:00Z");
            var payload = new ApprovePolicyChangeOperatorIntentPayload(sha, "Owner reviewed these protected fields.");
            var intent = new OperatorIntentRecord(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
                OperatorIntentVerbs.ApprovePolicyChange, Goal.Id.Value, null, "{}", [], "owner", "cli", "local-process", now,
                ActorKind: OperatorActorKind.Human);
            OperatorIntentPolicyApproval.Apply(Decisions, Goal, intent, payload, now, fingerprint);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
