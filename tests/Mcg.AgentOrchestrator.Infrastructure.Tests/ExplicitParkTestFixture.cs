using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Each fixture owns its SQLite file and injected operations; no processes or ambient changes.
internal sealed class ExplicitParkTestFixture : IDisposable
{
    internal string Root { get; } = Directory.CreateTempSubdirectory("park-").FullName;
    internal SqliteOrchestratorStateRepository Repository { get; }
    internal string DatabasePath => OrchestratorWorkspace.ForDirectory(Root).SqliteStatePath;
    internal AgentOrchestratorKernel Kernel { get; private set; } = new();
    internal Goal Goal { get; private set; }
    internal TaskSpec Task => Goal.Tasks.Single();
    internal int Dispatches, Acceptances, Landings;
    internal static readonly ConductorAutonomyPolicy Policy =
        ConductorAutonomyPolicy.Conservative with { MaxCriterionRetries = 1 };

    internal ExplicitParkTestFixture()
    {
        var path = DatabasePath;
        StateDbMigrations.EnsureUpToDate(path);
        Repository = new(path);
        Goal = Kernel.CreateGoal("Preserve explicit disposition",
            [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        Kernel.ActivateGoal(Goal.Id, AgentCatalog.Default().Agents);
        Kernel.ReportTaskProgress(Goal.Id, Task.Id, WorkTaskStatus.Completed, "Done");
        Kernel.RecordTaskVerification(Goal.Id, Task.Id,
            new TaskVerificationRecord("verify", Root, 0, "passed", "", DateTimeOffset.UnixEpoch));
        Kernel.BeginGoalAcceptanceVerification(Goal.Id, "Owned acceptance started");
        Assert.Equal(GoalStatus.Verifying, Goal.Status);
    }

    internal ConductorDriver Driver()
    {
        var driver = ConductorDriverTests.MakeDriver(
            getFacts: _ => new(WorkspaceExists: true),
            dispatchAndStart: _ => { Dispatches++; return DispatchStartOutcome.Started(); },
            startRecordedDispatches: _ => { Dispatches++; return DispatchStartOutcome.Started(); },
            runAcceptanceSummary: _ => { Acceptances++; return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria; },
            runAcceptanceVerificationWithLease: (_, _, _, _) =>
            { Acceptances++; return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria; },
            land: g => { Landings++; return new(g.Id.Value, g.Id.Value[..8],
                new LandingDecision.Promote(), "integration", true, "Landed"); },
            retryTaskWithCause: (id, task, message, round, cause) =>
                Kernel.RetryTask(id, task, message, cause, retryRoundKind: round),
            recordCriterionRetryFeedback: Kernel.RecordCriterionRetryFeedback,
            recordAcceptanceFailure: (g, checks, branch, main, attrs, baseline) =>
                Kernel.RecordAcceptanceFailure(g.Id, checks, branch, main, attrs, baseline),
            normalizeLifecycleState: (g, reason) => Kernel.NormalizeGoalLifecycleState(g.Id, reason));
        driver.BeginTick(Kernel, 1);
        return driver;
    }

    internal async Task Park()
    {
        var outcome = CliPersistentStateRunner.ExecuteGoalParkApplication(
            new CliCommandHandlers.GoalParkCommand(Goal.Id.Value, "Supported operator park", true),
            Goal.Id, Repository);
        Assert.True(outcome.ShouldSave);
        Assert.Equal(GoalStatus.Parked, (await Repository.LoadGoalAsync(Goal.Id))!.Status);
    }

    internal async Task Unpark()
    {
        await Repository.TransactGoalStateAsync(Goal.Id, (state, _) =>
        {
            var operatorKernel = AgentOrchestratorKernel.FromSnapshot(
                new OrchestratorSnapshot([state!.Goal], state.HumanInputRequests));
            operatorKernel.UnparkGoal(Goal.Id, "Supported unpark");
            return System.Threading.Tasks.Task.FromResult((true,
                (GoalStateSnapshot?)new GoalStateSnapshot(operatorKernel.ExportGoalSnapshot(Goal.Id),
                    operatorKernel.ExportSnapshot().HumanInputRequests), true));
        });
    }

    internal async Task Reload()
    {
        // New repository and kernel, so aggregate hydration cannot hide a bad stored status.
        Kernel = await new SqliteOrchestratorStateRepository(DatabasePath).LoadAsync();
        Goal = Kernel.GetGoal(Goal.Id);
    }

    internal (ConductorParallelAcceptanceRunResult Run, ConductorParallelAcceptanceAttempt Attempt) Red()
    {
        var candidate = ConductorParallelAcceptanceCandidate.Create(Goal, 0,
            ["src/Changed.cs"], "branch-identity", "main-identity");
        var acceptance = new AcceptanceVerificationSummary(false,
            [new AcceptanceCheckResult("focused RED", false, 1, "failure detail")],
            "failure detail", ["focused RED"], "branch-identity", "main-identity",
            CheckAttributions: [new AcceptanceCheckAttribution("focused RED",
                AcceptanceFailureOrigin.Introduced, "failure detail")],
            BaselineAttestation: "baseline-receipt");
        var attempt = new ConductorParallelAcceptanceAttempt("terminal-attempt", Goal.Id.Value,
            Goal.Id.Value[..8], 0, candidate.BranchHeadSha, candidate.MainHeadSha,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1,
            ConductorParallelAcceptanceAttemptOutcome.Failed,
            "stdout", "stderr", "exit", "heartbeat", "result", "metadata");
        return (ConductorParallelAcceptanceRunResult.Accepted(candidate, acceptance), attempt);
    }

    internal ConductorAdvanceResult ReconcileRed(ConductorDriver driver)
    {
        var (run, attempt) = Red();
        Assert.Equal("terminal-attempt", attempt.AttemptId);
        Assert.Equal(Goal.Id.Value, run.Candidate.Goal.Id.Value);
        Assert.Equal(attempt.BranchHeadSha, run.Candidate.BranchHeadSha);
        Assert.Equal(attempt.MainHeadSha, run.Candidate.MainHeadSha);
        ConductorBatchLoop.ReconcileParallelAcceptanceTerminalState(Kernel, Goal, run, attempt);
        var result = ConductorBatchLoop.CompleteParallelAcceptanceRun(driver, Policy, run, attempt, out var leaseHeld);
        Assert.False(leaseHeld);
        return result;
    }

    internal void AssertFailure()
    {
        var failure = Assert.IsType<AcceptanceFailureSummary>(Goal.RetainedAcceptanceFailure);
        Assert.Equal(new[] { "focused RED" }, failure.FailedChecks);
        Assert.Equal("branch-identity", failure.BranchHeadSha);
        Assert.Equal("main-identity", failure.MainHeadSha);
        Assert.Equal("baseline-receipt", failure.BaselineAttestation);
        var attribution = Assert.Single(failure.CheckAttributions!);
        Assert.Equal("focused RED", attribution.CheckName);
        Assert.Equal(AcceptanceFailureCause.NotClassified, attribution.Cause);
        Assert.Equal("failure detail", attribution.Evidence);
        Assert.Equal(AcceptanceFailureOrigin.Introduced, attribution.Origin);
        Assert.Equal(1, Goal.AutomaticAcceptanceRetryCount);
        Assert.Equal(0, Goal.OperatorAcceptanceRegateCount);
        Assert.Equal(1, Task.CriterionRetryCount);
        Assert.True(Goal.AcceptanceFailureDeferredForRetry);
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
