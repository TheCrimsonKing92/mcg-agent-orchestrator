using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;
using Fixture = ConductorDriverTestsAttributionPending.Fixture;

// Each case owns its SQLite store, TRX receipts and injected operations; parallel-safe.
public sealed class ConductorDriverTestsExplicitParkAttributionPending
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reconcile_ParkAndPendingInEitherOrder_RetainsBothAfterReload(bool parkFirst)
    {
        using var fixture = new Fixture();
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.Root);
        StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        var kernel = fixture.Kernel;
        var goal = fixture.Goal;
        Assert.True(kernel.BeginGoalAcceptanceVerification(goal.Id, "Owned native attempt"));
        await repository.SaveAsync(kernel);
        if (parkFirst)
        {
            Park(repository, goal.Id);
            kernel = await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).LoadAsync();
            goal = kernel.GetGoal(goal.Id);
            Assert.Equal(GoalStatus.Parked, goal.Status);
            Assert.Equal(GoalStatus.Verifying, goal.LifecycleStatus);
        }

        // Use the native result adapter, including its attempt binding, before the actual
        // batch reconciliation and landing-classification paths consume the terminal RED.
        var verification = new AcceptanceVerificationResult(false, false, 1, "executed RED",
            Checks: [fixture.Check], TestResultPaths: fixture.Check.TestResultPaths)
            { AttemptId = Fixture.Attempt };
        var summary = AcceptanceVerificationSummary.FromVerification(verification, [fixture.Check],
            [fixture.Check.Name], Fixture.Candidate, Fixture.Main, verification.TestResultPaths,
            [new AcceptanceCheckAttribution(fixture.Check.Name, AcceptanceFailureOrigin.Unattributed,
                ConductorDriverTestsAttributionPending.BaselineUnavailable)], "unattested baseline");
        Assert.Equal(Fixture.Attempt, summary.AttemptId);
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/Changed.cs"],
            Fixture.Candidate, Fixture.Main);
        var attempt = new ConductorParallelAcceptanceAttempt(Fixture.Attempt, goal.Id.Value,
            goal.Id.Value[..8], 0, Fixture.Candidate, Fixture.Main,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1,
            ConductorParallelAcceptanceAttemptOutcome.Failed,
            "stdout", "stderr", "exit", "heartbeat", "result", "metadata");
        var run = ConductorParallelAcceptanceRunResult.Accepted(candidate, summary);
        var gates = 0;
        var retries = 0;
        var dispatches = 0;
        var landings = 0;
        var driver = MakeDriver(
            runAcceptanceSummary: _ => { gates++; return summary; },
            dispatchAndStart: _ => { dispatches++; return DispatchStartOutcome.Started(); },
            land: g => { landings++; return new(g.Id.Value, g.Id.Value[..8],
                new LandingDecision.Promote(), "integration", true, "Landed"); },
            retryTaskWithCause: (_, _, _, _, _) =>
                { retries++; throw new InvalidOperationException("Unexpected Developer retry"); },
            recordAcceptanceFailure: (g, checks, branch, main, attrs, baseline) =>
                kernel.RecordAcceptanceFailure(g.Id, checks, branch, main, attrs, baseline),
            normalizeLifecycleState: (g, reason) => kernel.NormalizeGoalLifecycleState(g.Id, reason),
            getLandingFileScopes: _ => ["src/Changed.cs"], apparatusRedGate: fixture.Gate);
        driver.BeginTick(kernel, 1);
        ConductorBatchLoop.ReconcileParallelAcceptanceTerminalState(kernel, goal, run, attempt);
        Assert.Equal(GoalStatus.AcceptanceFailed, goal.LifecycleStatus);
        var occurrence = goal.LatestAcceptanceFailure!.OccurredAt;
        var completion = ConductorBatchLoop.CompleteParallelAcceptanceRun(driver,
            ConductorAutonomyPolicy.Conservative, run, attempt, out var leaseHeld);
        Assert.False(leaseHeld);
        var pendingHold = Assert.IsType<ConductorAdvanceOutcome.Held>(completion.Outcome);
        Assert.Equal(AcceptanceAttributionPending.EvidenceKind,
            pendingHold.Decision!.DiscriminatingEvidence);
        var pending = Assert.IsType<AcceptanceAttributionPending>(goal.LatestAcceptanceFailure!.AttributionPending);
        Assert.Equal(Fixture.Attempt, pending.AttemptId);
        Assert.Equal(Fixture.Candidate, pending.CandidateSha);
        Assert.Equal(Fixture.Main, pending.MainSha);
        Assert.Equal(occurrence, goal.LatestAcceptanceFailure.OccurredAt);
        Assert.Equal(parkFirst ? GoalStatus.Parked : GoalStatus.AcceptanceFailed, goal.Status);
        var serializedPending = System.Text.Json.JsonSerializer.Serialize(pending);
        await repository.SaveAsync(kernel);
        if (!parkFirst) Park(repository, goal.Id);

        kernel = await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).LoadAsync();
        goal = kernel.GetGoal(goal.Id);
        driver.BeginTick(kernel, 2);
        Assert.Equal(GoalStatus.Parked, goal.Status);
        Assert.Equal(GoalStatus.AcceptanceFailed, goal.ParkedLifecycleStatus);
        Assert.Equal(new GoalExplicitDisposition(1, GoalExplicitDispositionAction.Park), goal.ExplicitDisposition);
        Assert.Equal(serializedPending, System.Text.Json.JsonSerializer.Serialize(
            goal.LatestAcceptanceFailure!.AttributionPending));
        Assert.Equal(occurrence, goal.LatestAcceptanceFailure.OccurredAt);
        Assert.Equal(new[] { fixture.Check.Name }, goal.LatestAcceptanceFailure.FailedChecks);
        Assert.Equal(Fixture.Candidate, goal.LatestAcceptanceFailure.BranchHeadSha);
        Assert.Equal(Fixture.Main, goal.LatestAcceptanceFailure.MainHeadSha);
        Assert.Equal("unattested baseline", goal.LatestAcceptanceFailure.BaselineAttestation);
        var attribution = Assert.Single(goal.LatestAcceptanceFailure.CheckAttributions!);
        Assert.Equal(AcceptanceFailureOrigin.Unattributed, attribution.Origin);
        Assert.Equal(ConductorDriverTestsAttributionPending.BaselineUnavailable, attribution.Evidence);
        var parked = Assert.IsType<ConductorAdvanceOutcome.Held>(
            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).Outcome);
        Assert.Equal("Goal is parked; explicit unpark is required before admission.", parked.Reason);

        var unparked = CliPersistentStateRunner.ExecuteGoalUnparkApplication(
            new CliCommandHandlers.GoalUnparkCommand(goal.Id.Value, "Release explicit park", true),
            goal.Id, repository);
        Assert.Equal(CliCommandHandlers.GoalLifecycleTransitionDisposition.Applied, unparked.Disposition);
        kernel = await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).LoadAsync();
        goal = kernel.GetGoal(goal.Id);
        driver.BeginTick(kernel, 3);
        Assert.Equal(new GoalExplicitDisposition(2, GoalExplicitDispositionAction.Unpark), goal.ExplicitDisposition);
        Assert.Null(goal.ParkedLifecycleStatus);
        var held = ConductorDriverTestsAttributionPending.AssertPending(
            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative), goal);
        Assert.Equal(pendingHold.StableIdentity, held.StableIdentity);
        Assert.Equal(serializedPending, System.Text.Json.JsonSerializer.Serialize(
            goal.LatestAcceptanceFailure!.AttributionPending));
        Assert.Equal(0, gates);
        Assert.Equal(0, retries);
        Assert.Equal(0, dispatches);
        Assert.Equal(0, landings);
        Assert.Equal(0, goal.AutomaticAcceptanceRetryCount);
        Assert.Equal(0, goal.OperatorAcceptanceRegateCount);
        Assert.Equal(0, goal.Tasks.Single().CriterionRetryCount);
        Assert.False(goal.AcceptanceFailureDeferredForRetry);
        Assert.Equal(WorkTaskStatus.Completed, goal.Tasks.Single().Status);
    }

    private static void Park(SqliteOrchestratorStateRepository repository, GoalId goalId)
    {
        var outcome = CliPersistentStateRunner.ExecuteGoalParkApplication(
            new CliCommandHandlers.GoalParkCommand(goalId.Value, "Explicit operator park", true),
            goalId, repository);
        Assert.True(outcome.ShouldSave);
    }
}
