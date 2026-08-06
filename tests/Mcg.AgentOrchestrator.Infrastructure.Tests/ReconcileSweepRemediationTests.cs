using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Extensions.Configuration;

public sealed class ReconcileSweepRemediationTests
{
    [Xunit.Fact]
    public void DescriptorRendersOperatorCommandAndBuildsTypedInvocationFromSameGoal()
    {
        var goalId = GoalId.New();
        var remedy = TerminalGoalRemedy.Acceptance(
            goalId,
            goalId.Value[..8],
            new TerminalGoalGateArtifact("gate-1", "abc123", "gate-passed"));

        Xunit.Assert.Equal($"acceptance {goalId.Value[..8]}", remedy.RenderCommand());
        Xunit.Assert.Equal(["acceptance", goalId.Value], remedy.BuildInvocationArguments());
        Xunit.Assert.Equal(TerminalGoalRemedyVerb.Acceptance, remedy.Verb);
    }

    [Xunit.Fact]
    public void EmptyAllowlistEmitsBlockerAndEscalationOnceWithoutExecution()
    {
        var dbPath = NewDatabasePath();
        var calls = 0;
        var blocker = NewAcceptanceBlocker("same evidence");
        var sweep = Sweep(blocker);
        var options = new ReconcileSweepOptions(
            new HashSet<string>(StringComparer.Ordinal),
            3,
            TimeSpan.FromMinutes(5));

        var first = NewCoordinator(dbPath, options, blocker.Remedy.GateArtifact!.CandidateBranchSha, () => calls++).Process(sweep);
        var second = NewCoordinator(dbPath, options, blocker.Remedy.GateArtifact!.CandidateBranchSha, () => calls++).Process(sweep);

        Xunit.Assert.Equal(0, calls);
        Xunit.Assert.Single(first.Events, line => line.StartsWith("SWEEP_BLOCKER", StringComparison.Ordinal));
        Xunit.Assert.Single(first.Events, line => line.StartsWith("SWEEP_ESCALATION", StringComparison.Ordinal));
        Xunit.Assert.Empty(second.Events);
    }

    [Xunit.Fact]
    public void FailedRemedyConsumesBoundedBudgetAndEscalatesOnce()
    {
        var dbPath = NewDatabasePath();
        var calls = 0;
        var blocker = NewAcceptanceBlocker("persistent failure");
        var sweep = Sweep(blocker);
        var options = ReconcileSweepOptions.Default;
        var coordinator = new ReconcileSweepRemediationCoordinator(
            new ReconcileSweepRemediationStore(dbPath),
            options,
            _ =>
            {
                calls++;
                return new TerminalGoalRemedyExecutionResult(17, $"failure {calls}");
            },
            _ => blocker.Remedy.GateArtifact!.CandidateBranchSha);
        var events = new List<string>();

        for (var i = 0; i < 6; i++)
        {
            events.AddRange(coordinator.Process(sweep).Events);
        }

        Xunit.Assert.Equal(3, calls);
        Xunit.Assert.Equal(3, events.Count(line => line.StartsWith("SWEEP_REMEDY_ATTEMPT", StringComparison.Ordinal)));
        Xunit.Assert.Equal(3, events.Count(line => line.StartsWith("SWEEP_REMEDY_RESULT", StringComparison.Ordinal)));
        Xunit.Assert.Single(events, line => line.StartsWith("SWEEP_BLOCKER", StringComparison.Ordinal));
        Xunit.Assert.Single(events, line => line.StartsWith("SWEEP_ESCALATION", StringComparison.Ordinal));
        Xunit.Assert.Contains(events, line => line.Contains("exit=17", StringComparison.Ordinal) && line.Contains("failure 3", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void ChangedEvidenceCreatesNewNotificationState()
    {
        var dbPath = NewDatabasePath();
        var options = new ReconcileSweepOptions(
            new HashSet<string>(StringComparer.Ordinal),
            3,
            TimeSpan.FromMinutes(5));
        var firstBlocker = NewAcceptanceBlocker("evidence one");
        var secondBlocker = firstBlocker with { Evidence = "evidence two" };
        var coordinator = NewCoordinator(dbPath, options, firstBlocker.Remedy.GateArtifact!.CandidateBranchSha, () => { });

        var first = coordinator.Process(Sweep(firstBlocker));
        var repeated = coordinator.Process(Sweep(firstBlocker));
        var changed = coordinator.Process(Sweep(secondBlocker));

        Xunit.Assert.Contains(first.Events, line => line.StartsWith("SWEEP_BLOCKER", StringComparison.Ordinal));
        Xunit.Assert.Empty(repeated.Events);
        Xunit.Assert.Contains(changed.Events, line => line.StartsWith("SWEEP_BLOCKER", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void BranchShaMismatchDefaultDeniesAutoRemediation()
    {
        var dbPath = NewDatabasePath();
        var calls = 0;
        var blocker = NewAcceptanceBlocker("branch changed");
        var outcome = NewCoordinator(dbPath, ReconcileSweepOptions.Default, "different-sha", () => calls++)
            .Process(Sweep(blocker));

        Xunit.Assert.Equal(0, calls);
        Xunit.Assert.Contains(outcome.Events, line => line.Contains("reason=not-auto-runnable", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void AcceptanceLeaseSerializesManualAndAutomaticEntryPoints()
    {
        var store = new ReconcileSweepRemediationStore(NewDatabasePath());
        using var first = store.TryAcquireAcceptanceLease("goal-1", "owner-1", TimeSpan.FromMinutes(30));
        var overlapping = store.TryAcquireAcceptanceLease("goal-1", "owner-2", TimeSpan.FromMinutes(30));

        Xunit.Assert.NotNull(first);
        Xunit.Assert.Null(overlapping);
        first.Dispose();
        using var afterRelease = store.TryAcquireAcceptanceLease("goal-1", "owner-2", TimeSpan.FromMinutes(30));
        Xunit.Assert.NotNull(afterRelease);
    }

    [Xunit.Fact]
    public void ConfigurationAllowsExplicitEmptyAllowlist()
    {
        var values = new Dictionary<string, string?>
        {
            ["ReconcileSweep:AutoRemediationAllowlist:0"] = string.Empty,
            ["ReconcileSweep:MaximumAttempts"] = "2",
            ["ReconcileSweep:HeartbeatInterval"] = "00:10:00"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var options = ReconcileSweepConfiguration.Read(configuration.GetSection("ReconcileSweep"));

        Xunit.Assert.Empty(options.AutoRemediationAllowlist);
        Xunit.Assert.Equal(2, options.MaximumAttempts);
        Xunit.Assert.Equal(TimeSpan.FromMinutes(10), options.HeartbeatInterval);
    }

    private static ReconcileSweepRemediationCoordinator NewCoordinator(
        string dbPath,
        ReconcileSweepOptions options,
        string currentSha,
        Action executed) =>
        new(
            new ReconcileSweepRemediationStore(dbPath),
            options,
            _ =>
            {
                executed();
                return new TerminalGoalRemedyExecutionResult(0, "ok");
            },
            _ => currentSha);

    private static TerminalGoalSweepBlocker NewAcceptanceBlocker(string evidence)
    {
        var goalId = GoalId.New();
        return new TerminalGoalSweepBlocker(
            "completed-branch-unmerged",
            evidence,
            TerminalGoalRemedy.Acceptance(
                goalId,
                goalId.Value[..8],
                new TerminalGoalGateArtifact("gate-1", "candidate-sha", "gate-passed")));
    }

    private static TerminalGoalSweepResult Sweep(TerminalGoalSweepBlocker blocker) =>
        new([
            new TerminalGoalSweepGoalResult(
                blocker.Remedy.GoalId,
                blocker.Remedy.GoalPrefix,
                [],
                [blocker])
        ]);

    private static string NewDatabasePath()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-reconcile-sweep-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return Path.Combine(root, "state.db");
    }
}
