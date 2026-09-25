using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

public sealed class ConductorDriverTestsWithinAttemptRerunApparatus
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void PassingWithinAttemptRerunRegatesWithoutWorkerRetry(
        bool aggregate, bool workerRetriesExhausted)
    {
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var (kernel, goal) = SimpleGoal();
            var task = goal.Tasks.Single();
            PassVerification(kernel, goal, task);
            if (workerRetriesExhausted)
            {
                kernel.RecordCriterionRetryFeedback(goal.Id, task.Id, ["prior worker retry"]);
                kernel.RecordCriterionRetryFeedback(goal.Id, task.Id, ["prior worker retry"]);
            }
            var beforeWorkerRetries = goal.AutomaticAcceptanceRetryCount;
            var index = new AcceptanceFailingTestIndex(Path.Combine(
                root, "acceptance-gate-attempts", AcceptanceFailingTestIndex.FileName));
            var gate = new ApparatusRedGate(index, _ => root);
            var partition = MakePartitionVerdict(root, goal.Id);
            var checks = aggregate
                ? new[]
                {
                    partition,
                    new AcceptanceCheckResult("infrastructure tests", false, 1, "aggregate failed",
                        CoveredBy: [partition.Name])
                }
                : [partition];
            var acceptanceRuns = 0;
            var retryCalled = false;
            var escalationWritten = false;
            var driver = MakeDriver(
                getFacts: _ => GoalLifecycleFacts.None,
                runAcceptanceSummary: _ =>
                {
                    acceptanceRuns++;
                    return new AcceptanceVerificationSummary(
                        false, checks,
                        FailedChecks: checks.Select(check => check.Name).ToArray(),
                        BranchHeadSha: "candidate-a", MainHeadSha: "main-a");
                },
                retryTaskWithCause: (_, _, _, _, _) =>
                {
                    retryCalled = true;
                    throw new InvalidOperationException("Apparatus must not reopen a worker.");
                },
                recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
                writeEscalation: (_, _, _) => escalationWritten = true,
                getLandingFileScopes: _ => ["src/Unrelated.cs"],
                executionDirectory: root,
                apparatusRedGate: gate);

            var first = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(first.Outcome);
            Assert.Equal(GoalLifecycleState.Verified, held.State);
            Assert.Contains(AcceptanceWithinAttemptRerunEvidence.Reason, held.Reason, StringComparison.Ordinal);
            Assert.False(retryCalled);
            Assert.False(escalationWritten);
            Assert.Equal(beforeWorkerRetries, goal.AutomaticAcceptanceRetryCount);
            Assert.Equal(1, AcceptanceFailingTestIndex.CountRegates(index.Read(), goal.Id.Value));
            Assert.Equal(WorkTaskStatus.Completed, task.Status);

            var second = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            Assert.Equal(2, acceptanceRuns);
            Assert.IsType<ConductorAdvanceOutcome.Held>(second.Outcome);
            Assert.Equal(beforeWorkerRetries, goal.AutomaticAcceptanceRetryCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExhaustedApparatusBudgetEscalatesWithInfrastructureEvidence()
    {
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var (kernel, goal) = SimpleGoal();
            PassVerification(kernel, goal, goal.Tasks.Single());
            var index = new AcceptanceFailingTestIndex(Path.Combine(
                root, "acceptance-gate-attempts", AcceptanceFailingTestIndex.FileName));
            var partition = MakePartitionVerdict(root, goal.Id);
            var driver = MakeDriver(
                getFacts: _ => GoalLifecycleFacts.None,
                runAcceptanceSummary: _ => new AcceptanceVerificationSummary(false, [partition],
                    FailedChecks: [partition.Name], BranchHeadSha: "candidate-a", MainHeadSha: "main-a"),
                retryTaskWithCause: (_, _, _, _, _) =>
                    throw new InvalidOperationException("Budget exhaustion must not reopen a worker."),
                getLandingFileScopes: _ => ["src/Unrelated.cs"],
                executionDirectory: root,
                apparatusRedGate: new ApparatusRedGate(index, _ => root, perGoalRegateCap: 1));

            Assert.IsType<ConductorAdvanceOutcome.Held>(
                driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).Outcome);
            var exhausted = Assert.IsType<ConductorAdvanceOutcome.Escalated>(
                driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).Outcome);

            Assert.Contains(WithinAttemptRerunApparatusClassifier.BoundExhaustedToken,
                exhausted.Reason, StringComparison.Ordinal);
            Assert.Contains(partition.Name, exhausted.Reason, StringComparison.Ordinal);
            Assert.Contains(AcceptanceShardCompletionPredicates.MissingTrx,
                exhausted.Reason, StringComparison.Ordinal);
            Assert.Contains("each in-attempt rerun passed", exhausted.Reason, StringComparison.Ordinal);
            Assert.Equal(0, goal.AutomaticAcceptanceRetryCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static AcceptanceCheckResult MakePartitionVerdict(string root, GoalId goalId)
    {
        var partition = new GoalAcceptanceVerifier.AcceptanceManifestCheck
        {
            Name = "infrastructure tests: Process spawning",
            Type = "dotnet-test",
            Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
            Arguments = ["--filter", "FullyQualifiedName~ProcessSpawning"]
        };
        var cache = Assert.IsType<AcceptancePartitionVerdictCache>(AcceptancePartitionVerdictCache.Create(
            new AcceptancePartitionVerdictCacheOptions(
                goalId, root, [partition], 5, true,
                _ => "candidate", _ => "main", _ => "commit", () => "attempt", () => "manifest",
                () => false)));
        var first = new AcceptanceCheckResult(
            partition.Name, false, 0, "first run crashed without TRX",
            FailureClassification: AcceptanceShardCompletionPredicates.MissingTrx,
            CompletionDecision: new AcceptanceShardCompletionDecision(
                false, AcceptanceShardCompletionPredicates.MissingTrx, false, 0, 351, 0, "missing"));
        cache.RecordWithinAttemptRetry(
            partition, first, "attempt:partition:0", "attempt:partition:1",
            new AcceptanceRetainedDiagnostic("first-run.err", "first-run-sha"));
        var rerun = new AcceptanceCheckResult(
            partition.Name, true, 0, null,
            TestResultPaths: ["rerun.trx"], ExecutedTestCount: 351,
            CompletionDecision: new AcceptanceShardCompletionDecision(true, null, false, 0, 351, 351, "parsed"));
        var verdict = cache.SelectPartitionVerdict(partition, first, rerun);
        Assert.False(verdict.Passed);
        Assert.NotNull(verdict.WithinAttemptRerun);
        return verdict;
    }
}
