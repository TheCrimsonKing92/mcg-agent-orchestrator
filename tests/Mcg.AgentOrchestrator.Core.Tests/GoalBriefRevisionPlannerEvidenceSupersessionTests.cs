using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: every case owns an in-memory kernel and a fixed clock.
public sealed class GoalBriefRevisionPlannerEvidenceSupersessionTests
{
    [Theory]
    [InlineData("acceptance-gate")]
    [InlineData("reviewer")]
    [InlineData("operator")]
    public void ChangedCriteria_OlderAndIdentitylessWaits_CloseWithoutEvidence(string owner)
    {
        var (kernel, goal) = Create();
        var older = Request(kernel, goal, "older", 1, owner);
        var legacy = Request(kernel, goal, "legacy", null, owner);
        Assert.Equal(2, kernel.GetPendingHumanInput(goal.Id).Count);

        var result = kernel.ReviseGoalBrief(goal.Id, Brief("Revised criterion."), "Replace obsolete criteria");

        Assert.True(result.RefinedCriteriaReDerived);
        var expected = new[] { older.Id, legacy.Id }.OrderBy(id => id.Value, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, result.ClosedPlannerEvidenceRequestIds);
        Assert.Empty(kernel.GetPendingHumanInput(goal.Id));
        foreach (var request in new[] { older, legacy })
        {
            Assert.True(request.IsCompleted);
            Assert.False(request.WasDismissed);
            Assert.Null(request.AuthoritativeAnswer);
            Assert.Null(request.Answer);
            Assert.Empty(request.AnswerHistory);
            Assert.Empty(request.OperatorGates);
            var supersession = Assert.IsType<HumanInputBriefSupersession>(request.BriefSupersession);
            Assert.Equal(result.AuthoritativeVersion.Version, supersession.BriefVersion);
            Assert.Equal(result.AuthoritativeVersion.RecordedAt, supersession.SupersededAt);
            Assert.Contains("v2", supersession.Reason);
            Assert.Contains("Replace obsolete criteria", supersession.Reason);
        }
        Assert.Empty(goal.GateTestObligations);
        var progress = Assert.Single(goal.Timeline, item => item.Kind == ProgressKind.HumanInputSuperseded);
        Assert.Equal(
            $"kind=planner-evidence-superseded briefVersion=v2 requests={string.Join(',', expected.Select(id => id.Value))} " +
            $"reason={older.BriefSupersession!.Reason}", progress.Message);
        Assert.Empty(Assert.IsType<HumanInputSupersededPayload>(progress.HumanInputSuperseded).ResolvedStableIds);
    }

    [Fact]
    public void ChangedCriteria_IneligibleWaits_KeepTheirSnapshots()
    {
        var (kernel, goal) = Create();
        var eligible = Request(kernel, goal, "eligible", null);
        var otherKind = kernel.RequestHumanInput(goal.Id, null, "Clarify the scope");
        var completed = Request(kernel, goal, "answered", 1);
        kernel.SubmitHumanInput(completed.Id, "Recorded evidence");
        var dismissed = kernel.RequestHumanInput(goal.Id, null, "Dismissed evidence",
            kind: HumanWaitKind.ProspectiveAcceptanceEvidence, isDismissible: true);
        kernel.DismissHumanInput(dismissed.Id);
        var current = Request(kernel, goal, "new-brief", 2);
        var future = Request(kernel, goal, "future-brief", 3);
        var otherGoal = kernel.CreateGoal("Another goal");
        var unrelated = Request(kernel, otherGoal, "other-goal", null);
        var preserved = new[] { otherKind, completed, dismissed, current, future, unrelated };
        var before = preserved.Select(request => JsonSerializer.Serialize(request.ToSnapshot())).ToArray();

        var result = kernel.ReviseGoalBrief(goal.Id, Brief("Revised criterion."), "Scope changed");

        Assert.Equal([eligible.Id], result.ClosedPlannerEvidenceRequestIds);
        Assert.Equal(before, preserved.Select(request => JsonSerializer.Serialize(request.ToSnapshot())).ToArray());
        Assert.Contains(current, kernel.GetPendingHumanInput(goal.Id));
        Assert.Contains(otherKind, kernel.GetPendingHumanInput(goal.Id));
        Assert.Contains(unrelated, kernel.GetPendingHumanInput(otherGoal.Id));
    }

    [Theory]
    [InlineData("same-criteria")]
    [InlineData("no-declared-criteria")]
    [InlineData("no-refined-spec")]
    public void NoCriteriaDerivation_OpenEvidence_StaysUnchanged(string scenario)
    {
        var (kernel, goal) = Create(refined: scenario != "no-refined-spec");
        var older = Request(kernel, goal, "older", 1);
        var legacy = Request(kernel, goal, "legacy", null);
        var before = JsonSerializer.Serialize(kernel.ExportSnapshot().HumanInputRequests);
        var brief = scenario switch
        {
            "same-criteria" => "Additional context.\n" + Brief("Original criterion."),
            "no-declared-criteria" => "Reworded brief without declared criteria.",
            _ => Brief("Revised criterion.")
        };

        var result = kernel.ReviseGoalBrief(goal.Id, brief, "Only reword the brief");

        Assert.False(result.RefinedCriteriaReDerived);
        Assert.Empty(result.ClosedPlannerEvidenceRequestIds);
        Assert.Equal(before, JsonSerializer.Serialize(kernel.ExportSnapshot().HumanInputRequests));
        Assert.False(older.IsCompleted);
        Assert.False(legacy.IsCompleted);
        Assert.DoesNotContain(goal.Timeline, item => item.Kind == ProgressKind.HumanInputSuperseded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ChangedCriteria_MissingReason_StoresExplicitSupersession(string? reason)
    {
        var (kernel, goal) = Create();
        var wait = Request(kernel, goal, "legacy", null);

        kernel.ReviseGoalBrief(goal.Id, Brief("Revised criterion."), reason);

        Assert.True(wait.IsCompleted);
        Assert.Contains("v2", wait.BriefSupersession!.Reason);
        Assert.Contains("no revision reason supplied", wait.BriefSupersession.Reason);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Restore_ClosedEvidence_PreservesReasonAndAllowsFreshWait(bool json, bool typed)
    {
        var (kernel, goal) = Create();
        var wait = Request(kernel, goal, "same-evidence", typed ? 1 : null);
        kernel.ReviseGoalBrief(goal.Id, Brief("Revised criterion."), "Replace the original evidence");
        Assert.True(wait.IsCompleted);
        var snapshot = kernel.ExportSnapshot();
        if (json)
            snapshot = JsonSerializer.Deserialize<OrchestratorSnapshot>(JsonSerializer.Serialize(snapshot))!;

        var restored = AgentOrchestratorKernel.FromSnapshot(snapshot, new FixedClock());

        var closed = restored.GetHumanInputRequest(wait.Id);
        Assert.True(closed.IsCompleted);
        Assert.Equal(wait.BriefSupersession, closed.BriefSupersession);
        Assert.Null(closed.AuthoritativeAnswer);
        Assert.Null(closed.Answer);
        Assert.Empty(closed.AnswerHistory);
        Assert.Empty(restored.GetPendingHumanInput(goal.Id));
        var identity = typed ? wait.PlannerIdentity! with { BriefVersion = 2 } : null;
        var raised = restored.RequestHumanInputDeduplicated(goal.Id, null, wait.Question,
            kind: wait.Kind, questionFingerprint: wait.QuestionFingerprint,
            blockerFingerprint: "same-blocker", evidenceOwner: wait.EvidenceOwner,
            plannerIdentity: identity, plannerEvidence: wait.PlannerEvidence);
        Assert.True(raised.WasCreated);
        Assert.NotEqual(wait.Id, raised.Request.Id);
        Assert.False(raised.Request.IsCompleted);
        Assert.Null(raised.Request.BriefSupersession);
        Assert.Equal(raised.Request.Id, Assert.Single(restored.GetPendingHumanInput(goal.Id)).Id);
        Assert.True(closed.IsCompleted);
    }

    [Fact]
    public void InvalidRevision_OpenEvidence_IsNotPartiallySuperseded()
    {
        var (kernel, goal) = Create();
        Request(kernel, goal, "legacy", null);
        var before = JsonSerializer.Serialize(kernel.ExportSnapshot());

        Assert.Throws<KeyNotFoundException>(() => kernel.ReviseGoalBrief(goal.Id,
            Brief("Revised criterion."), "Invalid supersession",
            [new GoalBriefAnswerSupersession(HumanInputRequestId.New(), "replacement")]));

        Assert.Equal(before, JsonSerializer.Serialize(kernel.ExportSnapshot()));
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal) Create(bool refined = true)
    {
        var kernel = new AgentOrchestratorKernel(new FixedClock());
        var goal = kernel.CreateGoal(Brief("Original criterion."));
        if (refined)
            kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec("Original behavior", ["Original criterion."],
                VerificationClass.TestVerifiable, [], []));
        return (kernel, goal);
    }

    private static HumanInputRequest Request(AgentOrchestratorKernel kernel, Goal goal, string key,
        int? version, string owner = "reviewer") =>
        kernel.RequestHumanInputDeduplicated(goal.Id, null, $"Provide evidence for {key}",
            kind: HumanWaitKind.ProspectiveAcceptanceEvidence, blockerFingerprint: "same-blocker",
            evidenceOwner: owner,
            plannerIdentity: version is { } number ? new(goal.Id.Value, "planner-task", number, 1, key) : null,
            plannerEvidence: new(1, key, "post-implementation", owner, "Evidence", "Needed for acceptance", null, null, null)).Request;

    private static string Brief(string criterion) => $"Brief.\n\n## Acceptance criteria\n\n1. {criterion}\n";

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    }
}
