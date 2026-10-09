using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsParallelAcceptanceStaleIdentity(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    private const string Scope = "src/Mcg.AgentOrchestrator.App/Orchestration/StaleIdentity.cs";

    [Fact]
    public void ChangedIdentityArtifactReturnsGoalToVerifiedWithoutEscalation()
    {
        var attemptRoot = CreateTempDirectory("mcg-identity-stale-attempts");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateVerifiedSimpleGoal(kernel, $"Update {Scope}");
            var policy = ConductorAutonomyPolicy.Conservative;
            var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, [Scope]);
            var starter = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default,
                isProcessAlive: _ => true,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7103));
            var started = starter.Evaluate(candidate, policy, PassingRun);
            var child = new ConductorParallelAcceptanceAttemptCoordinator(attemptRoot, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, isProcessAlive: _ => false);
            child.RunAttemptForTests(
                started.Attempt,
                candidate,
                policy,
                (_, _, _, _, _) => throw new AcceptanceExecutionIdentityChangedException(
                    "main advanced during acceptance", isChangedIdentity: true));

            var escalations = new List<string>();
            var parent = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default,
                isProcessAlive: _ => false,
                launchOwnedProcess: _ => throw new InvalidOperationException("reconciliation must not launch"));
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
                writeEscalation: (_, _, reason) => escalations.Add(reason),
                getLandingFileScopes: _ => [Scope],
                parallelAcceptanceAttemptCoordinator: parent);

            var summary = new ConductorBatchLoop().Run(kernel, driver, policy, NoStopPath(), maxIterations: 1);
            var reconciled = ReadAttempt(started.Attempt.MetadataPath);

            Assert.Equal(1, summary.Held);
            Assert.Equal(GoalStatus.Verified, goal.Status);
            Assert.Empty(escalations);
            Assert.DoesNotContain(goal.Timeline, item => item.TickOutcome?.EscalationKind is not null);
            Assert.Contains(goal.Timeline, item =>
                item.Kind == ProgressKind.GoalPolicyDecision &&
                item.Message.Contains(started.Attempt.AttemptId, StringComparison.Ordinal) &&
                item.Message.Contains("main moved during the run", StringComparison.Ordinal));
            Assert.Equal(0, goal.AutomaticAcceptanceRetryCount);
            Assert.Equal(0, reconciled.TransientFailureCount);
            Assert.Equal(1, goal.ConsecutiveAcceptanceIdentityStaleCount);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Fact]
    public void UnresolvedIdentityArtifactStillEscalatesAsFault()
    {
        var attemptRoot = CreateTempDirectory("mcg-unresolved-identity-attempts");
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateVerifiedSimpleGoal(kernel, $"Update {Scope}");
            var policy = ConductorAutonomyPolicy.Conservative;
            var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, [Scope]);
            var starter = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default,
                isProcessAlive: _ => true,
                launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(7104));
            var started = starter.Evaluate(candidate, policy, PassingRun);
            var child = new ConductorParallelAcceptanceAttemptCoordinator(attemptRoot, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default, isProcessAlive: _ => false);
            child.RunAttemptForTests(
                started.Attempt,
                candidate,
                policy,
                (_, _, _, _, _) => throw new AcceptanceExecutionIdentityChangedException(
                    "identity could not be resolved", isChangedIdentity: false));

            var escalations = new List<string>();
            var parent = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot, Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default,
                isProcessAlive: _ => false,
                launchOwnedProcess: _ => throw new InvalidOperationException("reconciliation must not launch"));
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
                writeEscalation: (_, _, reason) => escalations.Add(reason),
                getLandingFileScopes: _ => [Scope],
                parallelAcceptanceAttemptCoordinator: parent);

            var summary = new ConductorBatchLoop().Run(kernel, driver, policy, NoStopPath(), maxIterations: 1);

            Assert.Equal(1, summary.Escalated);
            Assert.Equal(GoalStatus.AcceptanceFailed, goal.Status);
            Assert.Single(escalations);
            Assert.Equal(0, goal.ConsecutiveAcceptanceIdentityStaleCount);
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
        }
    }

    [Fact]
    public void ThirdConsecutiveStaleResultEscalatesAndNonStaleResultResetsCount()
    {
        var (kernel, goal) = SimpleGoal($"Update {Scope}");
        PassVerification(kernel, goal, goal.Tasks.Single());
        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, [Scope]);
        var escalations = new List<string>();
        var driver = MakeDriver(writeEscalation: (_, _, reason) => escalations.Add(reason));
        var policy = ConductorAutonomyPolicy.Conservative;

        for (var number = 1; number <= 2; number++)
        {
            var (run, attempt) = StaleResult(candidate, $"stale-{number}");
            driver.BeginTick(kernel, number);
            ConductorBatchLoop.ReconcileParallelAcceptanceTerminalState(kernel, goal, run, attempt);
            var result = ConductorBatchLoop.CompleteParallelAcceptanceRun(driver, policy, run, attempt, out _);
            Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
            Assert.Equal(GoalStatus.Verified, goal.Status);
            Assert.Equal(number, goal.ConsecutiveAcceptanceIdentityStaleCount);
            Assert.Empty(escalations);
        }

        Assert.Equal(2, kernel.ExportSnapshot().Goals.Single().ConsecutiveAcceptanceIdentityStaleCount);
        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        Assert.Equal(2, restored.GetGoal(goal.Id).ConsecutiveAcceptanceIdentityStaleCount);
        Assert.Equal("stale-2", restored.GetGoal(goal.Id).LastAcceptanceIdentityStaleAttemptId);

        var nonStaleAttempt = Attempt(goal, "non-stale");
        var nonStale = ConductorParallelAcceptanceRunResult.Fault(candidate, new OperationCanceledException("cancelled"));
        driver.BeginTick(kernel, 3);
        ConductorBatchLoop.ReconcileParallelAcceptanceTerminalState(kernel, goal, nonStale, nonStaleAttempt);
        ConductorBatchLoop.CompleteParallelAcceptanceRun(driver, policy, nonStale, nonStaleAttempt, out _);
        Assert.Equal(0, goal.ConsecutiveAcceptanceIdentityStaleCount);

        for (var number = 1; number <= 3; number++)
        {
            var (run, attempt) = StaleResult(candidate, $"again-{number}");
            driver.BeginTick(kernel, number + 3);
            ConductorBatchLoop.ReconcileParallelAcceptanceTerminalState(kernel, goal, run, attempt);
            var result = ConductorBatchLoop.CompleteParallelAcceptanceRun(driver, policy, run, attempt, out _);
            if (number < 3)
            {
                Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
                Assert.Equal(GoalStatus.Verified, goal.Status);
            }
            else
            {
                var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
                Assert.Contains("3 consecutive", escalated.Reason);
                Assert.Contains(attempt.AttemptId, escalated.Reason);
                Assert.Equal(GoalStatus.AcceptanceFailed, goal.Status);
                Assert.Single(escalations);
            }
        }
    }

    [Fact]
    public void ManualRetryAfterCappedStaleResultAllowsNextStaleRunToRegate()
    {
        var (kernel, goal) = SimpleGoal($"Update {Scope}");
        var task = goal.Tasks.Single();
        PassVerification(kernel, goal, task);
        kernel.BeginGoalAcceptanceVerification(goal.Id, "Background gate started.");
        for (var number = 1; number <= 3; number++)
        {
            kernel.RecordAcceptanceIdentityStale(goal.Id, $"prior-{number}");
        }

        kernel.ReconcileGoalAcceptanceFailed(goal.Id, ["stale cap reached"], "Stale cap reached.");
        kernel.RetryTask(goal.Id, task.Id, "Mechanically reopen after stale cap.");
        PassVerification(kernel, goal, task);
        Assert.Equal(GoalStatus.Verified, goal.Status);

        var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, [Scope]);
        var (run, attempt) = StaleResult(candidate, "after-reopen");
        var escalations = new List<string>();
        var driver = MakeDriver(writeEscalation: (_, _, reason) => escalations.Add(reason));
        driver.BeginTick(kernel, 1);

        ConductorBatchLoop.ReconcileParallelAcceptanceTerminalState(kernel, goal, run, attempt);
        var result = ConductorBatchLoop.CompleteParallelAcceptanceRun(
            driver, ConductorAutonomyPolicy.Conservative, run, attempt, out _);

        Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Equal(GoalStatus.Verified, goal.Status);
        Assert.Equal(1, goal.ConsecutiveAcceptanceIdentityStaleCount);
        Assert.Empty(escalations);
    }

    private static (ConductorParallelAcceptanceRunResult Run, ConductorParallelAcceptanceAttempt Attempt) StaleResult(
        ConductorParallelAcceptanceCandidate candidate,
        string id) =>
        (ConductorParallelAcceptanceRunResult.Fault(
            candidate,
            new AcceptanceExecutionIdentityChangedException("main moved", isChangedIdentity: true)),
            Attempt(candidate.Goal, id));

    private static ConductorParallelAcceptanceAttempt Attempt(Goal goal, string id) =>
        new(
            id, goal.Id.Value, goal.Id.Value[..8], 0, "branch", "main",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Environment.ProcessId,
            ConductorParallelAcceptanceAttemptOutcome.Failed,
            string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
}
