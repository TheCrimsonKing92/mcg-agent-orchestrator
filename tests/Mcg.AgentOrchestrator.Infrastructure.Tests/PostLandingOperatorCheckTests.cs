using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: in-memory kernels and injected process snapshots only.
public sealed class PostLandingOperatorCheckTests
{
    internal const string IncidentNeeded = "With both conductors on the new build, the schema-drift conduct-events log shows ...";
    private static readonly DateTimeOffset RecordedAt = DateTimeOffset.UnixEpoch.AddDays(1);

    [Theory]
    [InlineData("post-landing", "operator")]
    [InlineData("POST-LANDING", " Operator ")]
    public void Parser_CanonicalizesPostLandingOperatorCheck(string availability, string owner)
    {
        var parsed = AgentOutputDirectives.ParseHumanInputRequest(Directive(availability, owner), AgentRole.Planner);

        Assert.False(parsed.IsMalformed, parsed.Diagnostic);
        var directive = Assert.Single(parsed.Directives);
        Assert.Equal(HumanWaitKind.ProspectiveAcceptanceEvidence, directive.Kind);
        Assert.Equal("operator", directive.EvidenceOwner);
        Assert.Equal("post-landing", directive.PlannerEvidence!.Availability);
        Assert.Equal("operator", directive.PlannerEvidence.Owner);
        Assert.Contains("post-landing", directive.Question, StringComparison.Ordinal);
        Assert.Contains("never holds landing", directive.Question, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("reviewer", null, "owner=operator")]
    [InlineData("acceptance-gate", null, "owner=operator")]
    [InlineData(null, null, "owner=operator")]
    [InlineData("operator", "store", "must not name a store")]
    [InlineData("operator", "tests", "tests requires availability=post-implementation")]
    [InlineData("operator", "store_ref", "store_ref requires availability=retrievable")]
    public void Parser_RejectsInvalidPostLandingFields(string? owner, string? extra, string diagnostic)
    {
        var parsed = AgentOutputDirectives.ParseHumanInputRequest(Directive("post-landing", owner, extra), AgentRole.Planner);

        Assert.True(parsed.IsMalformed);
        Assert.Empty(parsed.Directives);
        Assert.Contains(diagnostic, parsed.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void Parser_UnknownAvailabilityNamesAllFourChoices()
    {
        var parsed = AgentOutputDirectives.ParseHumanInputRequest(Directive("unknown"), AgentRole.Planner);
        Assert.True(parsed.IsMalformed);
        Assert.Contains("retrievable, never-recorded, post-implementation, or post-landing", parsed.Diagnostic);
    }

    [Theory]
    [InlineData("post-landing", 0)]
    [InlineData("post-implementation", 1)]
    public void Dispatch_LeavesWaitPendingAndCountsOnlyLandingHolds(string availability, int expectedHolds)
    {
        var kernel = new AgentOrchestratorKernel(new FixedClock());
        var planner = new TaskSpec(TaskId.New(), "Plan", AgentRole.Planner);
        var goal = kernel.CreateGoal("Operator check", [planner, new(TaskId.New(), "Implement", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        kernel.RecordTaskDispatch(goal.Id, planner.Id, new TaskDispatchRecord("planner", "plan", "C:\\repo", RecordedAt));
        var output = Directive(availability) + "\nWORKER_RESULT:\nfiles: none\ncommands: inspect\ntests: not-run\ncommit: none\nblockers: none\nmodel_fit: adequate\nskills: none\nconfidence: high\nEND_WORKER_RESULT";

        kernel.RecordDispatchExecutionResult(goal.Id, planner.Id, new TaskVerificationRecord(
            "plan", "C:\\repo", 0, output, "", RecordedAt, WorkerResultPresent: true));

        var wait = Assert.Single(kernel.GetPendingHumanInput(goal.Id));
        Assert.Equal(availability == "post-landing", PostLandingOperatorCheck.IsPostLandingCheck(wait));
        Assert.False(wait.IsCompleted);
        Assert.NotEqual(WorkTaskStatus.WaitingForHuman, planner.Status);
        var summary = kernel.BuildGoalAcceptanceSummary(goal.Id);
        Assert.Equal(expectedHolds, summary.PendingHumanInputCount);
        Assert.Equal(expectedHolds, summary.Blockers.Count(blocker => blocker.Kind == GoalAcceptanceBlockerKind.PendingHumanInput));
    }

    [Theory]
    [InlineData("post-landing", false)]
    [InlineData("post-implementation", true)]
    public void GroupedAdmission_PreservesReadyOnlyForPostLanding(string availability, bool held)
    {
        var (kernel, goal) = Setup();
        var wait = Request(kernel, goal, availability);
        var ready = new GateReadyCandidateProjectionResult.Ready(new(
            goal.Id, GoalLifecycleState.Verified, GateReadyVerificationState.Satisfied, ChangeRiskTier.Behavior,
            ConductorTransitionDecision.Auto, ["src/check.cs"], ["resource:check"],
            new GateReadyMergeEvidence(new string('b', 40), new string('a', 40),
                GateReadyMergeStatus.Clean, GateReadyMergeReason.NoConflictsDetected)));

        var pending = kernel.GetPendingHumanInput(goal.Id);
        Assert.Same(held ? wait : null, GroupedAdmissionLandingHold.FindBlockingWait(pending));
        var result = GroupedAdmissionLandingHold.Apply(ready, pending);
        if (held)
        {
            var excluded = Assert.IsType<GateReadyCandidateProjectionResult.Excluded>(result);
            Assert.Equal(GateReadyCandidateExclusionReason.LandingHeld, excluded.Reason);
            Assert.Equal(wait.Id.Value, excluded.HoldRequestId);
        }
        else
            Assert.Same(ready, result);
    }

    [Theory]
    [InlineData("post-landing", false)]
    [InlineData("post-implementation", true)]
    public void CompletedDisposition_OnlyOrdinaryWaitRaisesStaleAlarm(string availability, bool stale)
    {
        var (kernel, goal) = Setup();
        Request(kernel, goal, availability);
        var snapshot = kernel.ExportSnapshot();
        var restored = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = [snapshot.Goals.Single() with { Status = GoalStatus.Completed }]
        });
        Assert.Equal(GoalStatus.Completed, restored.GetGoal(goal.Id).Status);
        Assert.Single(restored.GetPendingHumanInput(goal.Id));

        var disposition = Assert.Single(ConductorOperatorDispositionSnapshots.Build(restored, null,
            () => new ProcessCommandLineSnapshot(new Dictionary<int, string>())));

        Assert.Equal(stale, disposition.Blockers.Contains("stale-terminal-human-wait"));
    }

    [Fact]
    public void Helpers_UseExplicitKindAndAvailabilityAndPreserveSuppliedOrder()
    {
        var (kernel, goal) = Setup();
        var first = Request(kernel, goal, "post-landing");
        var ordinary = Request(kernel, goal, "post-implementation");
        var second = Request(kernel, goal, "POST-LANDING", "A second landed-build observation");
        var wrongKind = kernel.RequestHumanInputDeduplicated(goal.Id, null, "post-landing wording",
            kind: HumanWaitKind.SpecClarification, plannerEvidence: first.PlannerEvidence).Request;
        var noPayload = kernel.RequestHumanInputDeduplicated(goal.Id, null, "post-landing wording without payload",
            kind: HumanWaitKind.ProspectiveAcceptanceEvidence, evidenceOwner: "operator").Request;

        Assert.False(PostLandingOperatorCheck.IsPostLandingCheck(wrongKind));
        Assert.False(PostLandingOperatorCheck.IsPostLandingCheck(noPayload));
        Assert.Equal(new[] { ordinary, wrongKind, noPayload },
            PostLandingOperatorCheck.HoldingLanding([second, ordinary, wrongKind, first, noPayload]));
        Assert.Null(PostLandingOperatorCheck.DescribeOpen([ordinary, wrongKind, noPayload]));
        Assert.Equal($"open post-landing operator check(s): {second.Id.Value[..8]} (criterion 1: A second landed-build observation); " +
            $"{first.Id.Value[..8]} (criterion 1: {IncidentNeeded})",
            PostLandingOperatorCheck.DescribeOpen([second, ordinary, first]));
        Assert.Same(ordinary, GroupedAdmissionLandingHold.FindBlockingWait([second, ordinary, first]));
    }

    internal static string Directive(string availability, string? owner = "operator", string? extra = null,
        string needed = IncidentNeeded)
    {
        var payload = new Dictionary<string, object?>
        {
            ["criterion_index"] = 1, ["evidence_key"] = $"operator-check:{availability}:{needed}", ["availability"] = availability,
            ["owner"] = owner, ["needed"] = needed, ["reason"] = "Requires the appropriate runtime"
        };
        if (extra is not null) payload[extra] = extra == "tests" ? new[] { "ExampleTests" } : (object)"goal-events:incident";
        return "PLANNER_EVIDENCE_REQUEST: " + JsonSerializer.Serialize(payload);
    }

    internal static HumanInputRequest Request(AgentOrchestratorKernel kernel, Goal goal, string availability,
        string needed = IncidentNeeded)
    {
        var parsed = AgentOutputDirectives.ParseHumanInputRequest(Directive(availability, needed: needed), AgentRole.Planner);
        Assert.False(parsed.IsMalformed, parsed.Diagnostic);
        var directive = Assert.Single(parsed.Directives);
        return kernel.RequestHumanInputDeduplicated(goal.Id, null, directive.Question,
            questionFingerprint: directive.QuestionFingerprint, kind: directive.Kind,
            evidenceOwner: directive.EvidenceOwner, plannerEvidence: directive.PlannerEvidence).Request;
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal) Setup()
    {
        var kernel = new AgentOrchestratorKernel(new FixedClock());
        return (kernel, GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Operator checks"));
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => RecordedAt;
    }
}
