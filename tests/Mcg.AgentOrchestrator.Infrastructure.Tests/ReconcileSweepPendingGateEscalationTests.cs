using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ReconcileSweepPendingGateEscalationTests : IDisposable
{
    // Each fact owns a unique database directory, so this class is parallel-safe.
    private readonly string _databaseRoot = Path.Combine(
        Path.GetTempPath(), "mcg-reconcile-pending-gate-tests", Guid.NewGuid().ToString("N"));

    [Xunit.Fact]
    public void AcceptanceRemedyWithoutGateArtifactEmitsBlockerButNoEscalation()
    {
        var goalId = GoalId.New();
        var blocker = new TerminalGoalSweepBlocker(
            "completed-branch-unmerged",
            "verified goal still has unmerged branch goal/x; no passed acceptance outcome for candidate y",
            TerminalGoalRemedy.Acceptance(goalId, goalId.Value[..8], null));
        var calls = 0;
        var coordinator = NewCoordinator("candidate-sha", () => calls++);

        var outcome = coordinator.Process(Sweep(blocker));

        Xunit.Assert.Single(outcome.Events, line => line.StartsWith("SWEEP_BLOCKER", StringComparison.Ordinal));
        Xunit.Assert.DoesNotContain(outcome.Events, line => line.StartsWith("SWEEP_ESCALATION", StringComparison.Ordinal));
        Xunit.Assert.DoesNotContain(outcome.Events, line => line.StartsWith("SWEEP_REMEDY_ATTEMPT", StringComparison.Ordinal));
        Xunit.Assert.Equal(0, calls);
        Xunit.Assert.False(outcome.RemedySucceeded);
    }

    [Xunit.Fact]
    public void PendingHumanWaitStillEscalatesWithAttentionCommand()
    {
        var goalId = GoalId.New();
        var prefix = goalId.Value[..8];
        var command = $"attention show {prefix}; pending human waits ProspectiveAcceptanceEvidence a06c5007";
        var blocker = new TerminalGoalSweepBlocker(
            "completed-branch-unmerged",
            "verified goal has a pending human wait",
            TerminalGoalRemedy.OperatorOnly(goalId, prefix, command));
        var calls = 0;

        var outcome = NewCoordinator("candidate-sha", () => calls++).Process(Sweep(blocker));

        var escalation = Xunit.Assert.Single(outcome.Events, line => line.StartsWith("SWEEP_ESCALATION", StringComparison.Ordinal));
        Xunit.Assert.Contains("reason=not-auto-runnable", escalation, StringComparison.Ordinal);
        Xunit.Assert.Contains($"command={JsonSerializer.Serialize(command)}", escalation, StringComparison.Ordinal);
        Xunit.Assert.Equal(0, calls);
    }

    [Xunit.Fact]
    public void GateArtifactWithMismatchedCandidateStillEscalates()
    {
        var goalId = GoalId.New();
        var blocker = new TerminalGoalSweepBlocker(
            "completed-branch-unmerged",
            "passed gate is for a different branch candidate",
            TerminalGoalRemedy.Acceptance(
                goalId,
                goalId.Value[..8],
                new TerminalGoalGateArtifact("gate-1", "candidate-sha", "gate-passed")));
        var calls = 0;

        var outcome = NewCoordinator("different-sha", () => calls++).Process(Sweep(blocker));

        var escalation = Xunit.Assert.Single(outcome.Events, line => line.StartsWith("SWEEP_ESCALATION", StringComparison.Ordinal));
        Xunit.Assert.Contains("reason=not-auto-runnable", escalation, StringComparison.Ordinal);
        Xunit.Assert.Equal(0, calls);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void BlockerLineHasAcceptanceQueueOwnerOnlyWithoutGateArtifact(bool hasGateArtifact)
    {
        var goalId = GoalId.New();
        var prefix = goalId.Value[..8];
        var blocker = new TerminalGoalSweepBlocker(
            "completed-branch-unmerged", "e",
            TerminalGoalRemedy.Acceptance(goalId, prefix,
                hasGateArtifact ? new TerminalGoalGateArtifact("gate-1", "candidate-sha", "gate-passed") : null));

        var outcome = NewCoordinator("different-sha", () => { }).Process(Sweep(blocker));

        var line = Xunit.Assert.Single(outcome.Events, line => line.StartsWith("SWEEP_BLOCKER", StringComparison.Ordinal));
        var expected = $"SWEEP_BLOCKER goal={prefix} kind=completed-branch-unmerged evidence=\"e\" command=\"acceptance {prefix}\"";
        Xunit.Assert.Equal(expected + (hasGateArtifact ? "" : " owner=acceptance-queue"), line);
        if (hasGateArtifact)
        {
            Xunit.Assert.DoesNotContain("owner=", line, StringComparison.Ordinal);
        }
        else
        {
            Xunit.Assert.EndsWith(" owner=acceptance-queue", line);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void ConsoleBlockerLineHasAcceptanceQueueOwnerOnlyWithoutGateArtifact(bool hasGateArtifact)
    {
        var goalId = GoalId.New();
        var prefix = goalId.Value[..8];
        var blocker = new TerminalGoalSweepBlocker(
            "completed-branch-unmerged", "e",
            TerminalGoalRemedy.Acceptance(goalId, prefix,
                hasGateArtifact ? new TerminalGoalGateArtifact("gate-1", "candidate-sha", "gate-passed") : null));

        var output = CaptureConsole(() => ConsoleViews.PrintTerminalGoalSweep(Sweep(blocker)));

        var expected = $"SWEEP_BLOCKER goal={prefix} kind=completed-branch-unmerged evidence=\"e\" command=\"acceptance {prefix}\"";
        Xunit.Assert.Equal(expected + (hasGateArtifact ? "" : " owner=acceptance-queue") + Environment.NewLine, output);
    }

    private ReconcileSweepRemediationCoordinator NewCoordinator(string currentSha, Action executed)
    {
        Directory.CreateDirectory(_databaseRoot);
        return new ReconcileSweepRemediationCoordinator(
            new ReconcileSweepRemediationStore(Path.Combine(_databaseRoot, "state.db")),
            ReconcileSweepOptions.Default,
            _ =>
            {
                executed();
                return new TerminalGoalRemedyExecutionResult(0, "ok");
            },
            _ => currentSha);
    }

    private static TerminalGoalSweepResult Sweep(TerminalGoalSweepBlocker blocker) =>
        new([
            new TerminalGoalSweepGoalResult(
                blocker.Remedy.GoalId,
                blocker.Remedy.GoalPrefix,
                [],
                [blocker])
        ]);

    public void Dispose()
    {
        if (Directory.Exists(_databaseRoot))
        {
            Directory.Delete(_databaseRoot, recursive: true);
        }
    }
}
