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
    public void TransientSlotUnavailabilityDoesNotConsumeRemedyBudget()
    {
        var dbPath = NewDatabasePath();
        var calls = 0;
        var blocker = NewAcceptanceBlocker("slot contention then terminal failure");
        var coordinator = new ReconcileSweepRemediationCoordinator(
            new ReconcileSweepRemediationStore(dbPath),
            ReconcileSweepOptions.Default,
            _ => ++calls <= 5
                ? TerminalGoalRemedyExecutionResult.Retryable(75, $"slot unavailable {calls}")
                : new TerminalGoalRemedyExecutionResult(17, $"terminal failure {calls}"),
            _ => blocker.Remedy.GateArtifact!.CandidateBranchSha);
        var events = new List<string>();

        for (var i = 0; i < 10; i++)
        {
            events.AddRange(coordinator.Process(Sweep(blocker)).Events);
        }

        Xunit.Assert.Equal(8, calls);
        Xunit.Assert.Equal(8, events.Count(line => line.StartsWith("SWEEP_REMEDY_ATTEMPT", StringComparison.Ordinal)));
        Xunit.Assert.Equal(8, events.Count(line => line.StartsWith("SWEEP_REMEDY_RESULT", StringComparison.Ordinal)));
        Xunit.Assert.Equal(5, events.Count(line => line.Contains("retryable=true", StringComparison.Ordinal)));
        Xunit.Assert.Single(events, line => line.StartsWith("SWEEP_ESCALATION", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void PendingRemedyHoldsSharedAcceptanceLeaseUntilItsCurrentAttemptCompletes()
    {
        var dbPath = NewDatabasePath();
        var blocker = NewAcceptanceBlocker("background acceptance in progress");
        var executorCalls = 0;
        var store = new ReconcileSweepRemediationStore(dbPath);
        var coordinator = new ReconcileSweepRemediationCoordinator(
            store,
            ReconcileSweepOptions.Default,
            _ => ++executorCalls == 1
                ? TerminalGoalRemedyExecutionResult.Pending("background attempt started")
                : new TerminalGoalRemedyExecutionResult(0, "current background attempt landed"),
            _ => blocker.Remedy.GateArtifact!.CandidateBranchSha);

        var started = coordinator.Process(Sweep(blocker));
        using var overlap = store.TryAcquireAcceptanceLease(
            blocker.Remedy.GoalId.Value,
            "manual-operator",
            TimeSpan.FromMinutes(30));
        var completed = coordinator.Process(Sweep(blocker));
        using var afterCompletion = store.TryAcquireAcceptanceLease(
            blocker.Remedy.GoalId.Value,
            "manual-operator",
            TimeSpan.FromMinutes(30));

        Xunit.Assert.Null(overlap);
        Xunit.Assert.NotNull(afterCompletion);
        Xunit.Assert.Single(started.Events, line => line.StartsWith("SWEEP_REMEDY_ATTEMPT", StringComparison.Ordinal));
        Xunit.Assert.DoesNotContain(started.Events, line => line.StartsWith("SWEEP_REMEDY_RESULT", StringComparison.Ordinal));
        Xunit.Assert.Single(completed.Events, line => line.Contains("current background attempt landed", StringComparison.Ordinal));
        Xunit.Assert.True(completed.RemedySucceeded);
    }

    [Xunit.Fact]
    public void PendingRemedyCompletesAfterSuccessfulAcceptanceRemovesCurrentBlocker()
    {
        var dbPath = NewDatabasePath();
        var blocker = NewAcceptanceBlocker("background acceptance in progress");
        var executorCalls = 0;
        var store = new ReconcileSweepRemediationStore(dbPath);
        var coordinator = new ReconcileSweepRemediationCoordinator(
            store,
            ReconcileSweepOptions.Default,
            _ => ++executorCalls == 1
                ? TerminalGoalRemedyExecutionResult.Pending("background attempt started")
                : new TerminalGoalRemedyExecutionResult(0, "background attempt landed and blocker cleared"),
            _ => blocker.Remedy.GateArtifact!.CandidateBranchSha);

        var started = coordinator.Process(Sweep(blocker));
        var completed = coordinator.Process(new TerminalGoalSweepResult([]));
        using var afterCompletion = store.TryAcquireAcceptanceLease(
            blocker.Remedy.GoalId.Value,
            "manual-operator",
            TimeSpan.FromMinutes(30));

        Xunit.Assert.Single(started.Events, line => line.StartsWith("SWEEP_REMEDY_ATTEMPT", StringComparison.Ordinal));
        Xunit.Assert.Single(completed.Events, line => line.StartsWith("SWEEP_REMEDY_RESULT", StringComparison.Ordinal));
        Xunit.Assert.Contains(completed.Events, line => line.Contains("blocker cleared", StringComparison.Ordinal));
        Xunit.Assert.True(completed.RemedySucceeded);
        Xunit.Assert.NotNull(afterCompletion);
    }

    [Xunit.Fact]
    public void ReplaysOneHundredFortyBlockedRechecksAndRemediesOnFirstRecheckAfterCleanup()
    {
        var dbPath = NewDatabasePath();
        var blocker = NewAcceptanceBlocker("verified goal still has unmerged branch goal/4b57adc0");
        var blockerPresent = true;
        var rootCauseCleared = false;
        var calls = 0;
        var successRecheck = -1;
        var coordinator = new ReconcileSweepRemediationCoordinator(
            new ReconcileSweepRemediationStore(dbPath),
            ReconcileSweepOptions.Default,
            _ =>
            {
                calls++;
                if (!rootCauseCleared)
                {
                    return TerminalGoalRemedyExecutionResult.Retryable(75, "dirty worktree still blocks pre-merge rebase");
                }

                blockerPresent = false;
                return new TerminalGoalRemedyExecutionResult(0, "acceptance landed after dirty worktree cleanup");
            },
            _ => blocker.Remedy.GateArtifact!.CandidateBranchSha);
        var events = new List<string>();

        for (var recheck = 0; recheck < 140; recheck++)
        {
            if (recheck == 2)
            {
                rootCauseCleared = true;
            }

            var outcome = coordinator.Process(blockerPresent ? Sweep(blocker) : new TerminalGoalSweepResult([]));
            events.AddRange(outcome.Events);
            if (outcome.RemedySucceeded)
            {
                successRecheck = recheck;
            }
        }

        Xunit.Assert.Equal(2, successRecheck);
        Xunit.Assert.Equal(3, calls);
        Xunit.Assert.Single(events, line => line.StartsWith("SWEEP_BLOCKER", StringComparison.Ordinal));
        Xunit.Assert.Single(events, line => line.StartsWith("SWEEP_REMEDY_RESULT", StringComparison.Ordinal) && line.Contains("exit=0", StringComparison.Ordinal));
        Xunit.Assert.DoesNotContain(events, line => line.StartsWith("SWEEP_ESCALATION", StringComparison.Ordinal));
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
        var observed = store.TryGetAcceptanceLease("goal-1", TimeSpan.FromMinutes(30));

        Xunit.Assert.NotNull(first);
        Xunit.Assert.Null(overlapping);
        Xunit.Assert.NotNull(observed);
        Xunit.Assert.Equal("owner-1", observed.Owner);
        Xunit.Assert.Equal(observed.AcquiredAtUtc.AddMinutes(30), observed.ExpiresAtUtc);
        first.Dispose();
        using var afterRelease = store.TryAcquireAcceptanceLease("goal-1", "owner-2", TimeSpan.FromMinutes(30));
        Xunit.Assert.NotNull(afterRelease);
    }

    [Xunit.Fact]
    public void StoreAndReleasedAcceptanceLeaseDoNotRetainDatabaseFileHandles()
    {
        var dbPath = NewDatabasePath();
        var store = new ReconcileSweepRemediationStore(dbPath);

        using (var lease = store.TryAcquireAcceptanceLease(
                   "goal-1",
                   "owner-1",
                   TimeSpan.FromMinutes(30)))
        {
            Xunit.Assert.NotNull(lease);
        }

        Directory.Delete(Path.GetDirectoryName(dbPath)!, recursive: true);
        Xunit.Assert.False(Directory.Exists(Path.GetDirectoryName(dbPath)));
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
