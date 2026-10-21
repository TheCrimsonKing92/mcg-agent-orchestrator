using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: only in-memory kernels, with a fixed injected clock.
public sealed class GateTestObligationIntakeTests
{
    [Fact]
    public void LegacyDuplicateSnapshot_RetainsFirstSelection()
    {
        var (kernel, goal, _) = CreatePlanner();
        var snapshot = kernel.ExportSnapshot();
        var saved = snapshot.Goals.Single();
        var restored = AgentOrchestratorKernel.FromSnapshot(snapshot with { Goals = [saved with
        {
            GateTestObligations = [new("legacy", 1, ["FirstTests"], null, "first", RecordedAt),
                new("legacy", 1, ["SecondTests"], null, "second", RecordedAt)]
        }] });
        Assert.Equal(new[] { "FirstTests" }, Assert.Single(restored.GetGoal(goal.Id).GateTestObligations).Selectors);
    }

    private static readonly DateTimeOffset RecordedAt = DateTimeOffset.UnixEpoch.AddDays(1);

    [Theory]
    [InlineData("acceptance-gate")]
    [InlineData("Acceptance")]
    [InlineData(" ACCEPTANCE-GATE ")]
    public void GateOwner_RecordsPersistedObligationAndCompletesPlanner(string owner)
    {
        var (kernel, goal, planner) = CreatePlanner();
        var text = Directive(owner, ["AlphaTests", "BetaTests.Row"]);
        var directive = AgentOutputDirectives.ParseHumanInputRequest(text, AgentRole.Planner).Directive!;

        Record(kernel, goal, planner, text);

        var obligation = Assert.Single(goal.GateTestObligations);
        Assert.Equal(directive.QuestionFingerprint, obligation.Id);
        Assert.Equal(goal.AuthoritativeBrief.Version, obligation.BriefVersion);
        Assert.Equal(new[] { "AlphaTests", "BetaTests.Row" }, obligation.Selectors);
        Assert.Equal(planner.Id.Value, obligation.RequestingTaskId);
        Assert.Equal(directive.Question, obligation.Question);
        Assert.Equal(RecordedAt, obligation.RecordedAt);
        Assert.Empty(kernel.GetPendingHumanInput(goal.Id));
        Assert.Empty(kernel.ExportSnapshot().HumanInputRequests);
        Assert.Equal(WorkTaskStatus.Completed, planner.Status);
        var note = Assert.Single(goal.Timeline.Where(entry =>
            entry.Message.StartsWith("GATE_TEST_OBLIGATION_RECORDED", StringComparison.Ordinal)));
        Assert.Equal(ProgressKind.TaskNote, note.Kind);
        Assert.StartsWith($"GATE_TEST_OBLIGATION_RECORDED id={obligation.Id} brief_version={obligation.BriefVersion} selectors=2",
            note.Message, StringComparison.Ordinal);

        // Serialize as well as round-trip the public snapshot to avoid list reference equality.
        var snapshot = JsonSerializer.Deserialize<OrchestratorSnapshot>(JsonSerializer.Serialize(kernel.ExportSnapshot()))!;
        var restored = Assert.Single(AgentOrchestratorKernel.FromSnapshot(snapshot).GetGoal(goal.Id).GateTestObligations);
        Assert.Equal(obligation.Id, restored.Id);
        Assert.Equal(obligation.BriefVersion, restored.BriefVersion);
        Assert.Equal(obligation.Selectors, restored.Selectors);
        Assert.Equal(obligation.RequestingTaskId, restored.RequestingTaskId);
        Assert.Equal(obligation.Question, restored.Question);
        Assert.Equal(obligation.RecordedAt, restored.RecordedAt);
    }

    [Fact]
    public void OperatorOwner_KeepsProspectiveWait()
    {
        var (kernel, goal, planner) = CreatePlanner();
        Record(kernel, goal, planner, Directive("operator", ["AlphaTests"]));

        var wait = Assert.Single(kernel.GetPendingHumanInput(goal.Id));
        Assert.Equal(HumanWaitKind.ProspectiveAcceptanceEvidence, wait.Kind);
        Assert.Equal("operator", wait.EvidenceOwner);
        Assert.Single(kernel.ExportSnapshot().HumanInputRequests);
        Assert.Empty(goal.GateTestObligations);
    }

    [Fact]
    public void HumanInput_KeepsSpecClarificationWait()
    {
        var (kernel, goal, planner) = CreatePlanner();
        Record(kernel, goal, planner, "HUMAN_INPUT: Which behavior should be implemented?");

        Assert.Equal(HumanWaitKind.SpecClarification, Assert.Single(kernel.GetPendingHumanInput(goal.Id)).Kind);
        Assert.Single(kernel.ExportSnapshot().HumanInputRequests);
        Assert.Empty(goal.GateTestObligations);
    }

    [Fact]
    public void AbsentTests_RequiresExplicitProspectiveAnswer()
    {
        var (kernel, goal, planner) = CreatePlanner();
        Record(kernel, goal, planner, Directive("acceptance-gate", null));

        Assert.Empty(goal.GateTestObligations);
        Assert.Null(Assert.Single(kernel.GetPendingHumanInput(goal.Id)).TestNames);
        Assert.Single(kernel.ExportSnapshot().HumanInputRequests);
    }

    [Fact]
    public void RepeatedRequest_ReplacesWithinVersionAndRetainsPriorVersion()
    {
        var (kernel, goal, planner) = CreatePlanner();
        Record(kernel, goal, planner, Directive("acceptance-gate", ["AlphaTests"]));
        var first = Assert.Single(goal.GateTestObligations);
        var secondPlanner = kernel.AddTask(goal.Id, AgentRole.Planner, "Plan again",
            AgentCatalog.Default().Agents, beforeRole: AgentRole.Developer);
        Record(kernel, goal, secondPlanner, Directive("acceptance-gate", ["GammaTests"]));
        var replaced = Assert.Single(goal.GateTestObligations);
        Assert.Equal(first.Id, replaced.Id);
        Assert.Equal(first.BriefVersion, replaced.BriefVersion);
        Assert.Equal(new[] { "GammaTests" }, replaced.Selectors);

        kernel.ReviseGoalBrief(goal.Id, "Revised gate evidence brief");
        var thirdPlanner = kernel.AddTask(goal.Id, AgentRole.Planner, "Plan revised brief",
            AgentCatalog.Default().Agents, beforeRole: AgentRole.Developer);
        Record(kernel, goal, thirdPlanner, Directive("acceptance-gate", ["BetaTests.Row"]));

        Assert.Equal(2, goal.GateTestObligations.Count);
        Assert.Equal(new[] { first.BriefVersion, goal.AuthoritativeBrief.Version },
            goal.GateTestObligations.Select(item => item.BriefVersion));
        Assert.All(goal.GateTestObligations, item => Assert.Equal(first.Id, item.Id));
        Assert.Equal(new[] { "BetaTests.Row" }, goal.GateTestObligations[1].Selectors);
        Assert.Empty(kernel.GetPendingHumanInput(goal.Id));
    }

    internal static AgentOrchestratorKernel Seed(
        AgentOrchestratorKernel kernel, Goal goal, IReadOnlyList<string> selectors)
    {
        var snapshot = kernel.ExportSnapshot();
        return AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(item => item.Id == goal.Id.Value ? item with
            {
                GateTestObligations = [new("obligation-gate-test", goal.AuthoritativeBrief.Version,
                    selectors, null, "Candidate gate receipt", RecordedAt)]
            } : item).ToArray()
        });
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Planner) CreatePlanner()
    {
        var kernel = new AgentOrchestratorKernel(new FixedClock());
        var planner = new TaskSpec(TaskId.New(), "Plan", AgentRole.Planner);
        var goal = kernel.CreateGoal("Gate evidence", [planner, new(TaskId.New(), "Implement", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        return (kernel, goal, planner);
    }

    private static void Record(AgentOrchestratorKernel kernel, Goal goal, TaskSpec planner, string directive)
    {
        kernel.RecordTaskDispatch(goal.Id, planner.Id, new TaskDispatchRecord("planner", "plan", "C:\\repo", RecordedAt));
        var output = directive + "\nWORKER_RESULT:\nfiles: none\ncommands: inspect\ntests: not-run\ncommit: none\nblockers: none\nmodel_fit: adequate\nskills: none\nconfidence: high\nEND_WORKER_RESULT";
        kernel.RecordDispatchExecutionResult(goal.Id, planner.Id, new TaskVerificationRecord(
            "plan", "C:\\repo", 0, output, "", RecordedAt, WorkerResultPresent: true));
    }

    private static string Directive(string owner, string[]? selectors)
    {
        var payload = new Dictionary<string, object?>
        {
            ["criterion_index"] = 1, ["evidence_key"] = "gate", ["availability"] = "post-implementation",
            ["owner"] = owner, ["needed"] = "candidate receipt", ["reason"] = "candidate required"
        };
        if (selectors is not null) payload["tests"] = selectors;
        return "PLANNER_EVIDENCE_REQUEST: " + JsonSerializer.Serialize(payload);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => RecordedAt;
    }
}
