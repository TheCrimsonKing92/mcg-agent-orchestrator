using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

public sealed class ConductorDriverTestsMixedApparatusRegate
{
    private const string SourcePath =
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/GoalWorktreeTestsRebaseMergeMaterialization.cs";
    private const string Identity =
        "Mcg.AgentOrchestrator.Infrastructure.Tests.GoalWorktreeTestsRebaseMergeMaterialization.RepairsSplitCommitMaterializationAfterRebase";
    private const string CrashedName = "infrastructure tests: Goal acceptance verifier";
    private const string FailedName = "infrastructure tests: Goal worktree cleanup";

    [Fact]
    public void MixedWithinAttemptRerunAndCandidateRerunPassRegatesWithoutWorkerRetry()
    {
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            WriteTestSource(root);
            var index = CreateIndex(root);
            var (kernel, goal) = SimpleGoal();
            var task = goal.Tasks.Single();
            PassVerification(kernel, goal, task);
            var retryCalled = false;
            var driver = MakeDriver(
                getFacts: _ => GoalLifecycleFacts.None,
                runAcceptanceSummary: _ => RedSummary(MixedChecks()),
                retryTaskWithCause: (_, _, _, _, _) =>
                {
                    retryCalled = true;
                    throw new InvalidOperationException("A mixed apparatus RED must not reopen a Developer.");
                },
                recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
                getLandingFileScopes: _ => ["src/Changed.cs"],
                executionDirectory: root,
                apparatusRedGate: CreateGate(root, index));

            var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
            Assert.Equal(GoalLifecycleState.Verified, held.State);
            Assert.Contains(AcceptanceWithinAttemptRerunEvidence.Reason, held.Reason, StringComparison.Ordinal);
            Assert.Contains(ApparatusRedClassifier.CandidateRerunEvidenceKind, held.Reason, StringComparison.Ordinal);
            Assert.False(retryCalled);
            Assert.Equal(WorkTaskStatus.Completed, task.Status);
            Assert.Equal(0, task.CriterionRetryCount);
            Assert.Equal(0, goal.AutomaticAcceptanceRetryCount);
            Assert.Equal(1, AcceptanceFailingTestIndex.CountRegates(index.Read(), goal.Id.Value));
            var journal = GoalOperationJournal.Read(root, goal.Id).Entries;
            var regate = Assert.Single(journal.Where(entry =>
                entry.Operation == GoalOperationJournal.AcceptanceApparatusRegateOperation));
            Assert.Contains(AcceptanceWithinAttemptRerunEvidence.Reason, regate.Detail, StringComparison.Ordinal);
            Assert.Contains(ApparatusRedClassifier.CandidateRerunEvidenceKind, regate.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain(journal, entry =>
                entry.Operation == GoalOperationJournal.AcceptanceApparatusGenuineOperation);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FailedCandidateRerunStillRetriesDeveloper() => AssertRetriesDeveloper(Variant.FailedCandidateRerun);

    [Fact]
    public void IdentityInsideChangedPathsStillRetriesDeveloper() => AssertRetriesDeveloper(Variant.InsideChangedPaths);

    [Fact]
    public void MissingWithinAttemptEvidenceStillRetriesDeveloper() => AssertRetriesDeveloper(Variant.MissingWithinAttemptEvidence);

    [Fact]
    public void ZeroExecutedRerunTestsStillRetriesDeveloper() => AssertRetriesDeveloper(Variant.ZeroExecutedRerunTests);

    [Fact]
    public void MixedRegateUsesSharedPerGoalBound()
    {
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            WriteTestSource(root);
            var index = CreateIndex(root);
            var (kernel, goal) = SimpleGoal();
            var task = goal.Tasks.Single();
            PassVerification(kernel, goal, task);
            var driver = MakeDriver(
                getFacts: _ => GoalLifecycleFacts.None,
                runAcceptanceSummary: _ => RedSummary(MixedChecks()),
                retryTaskWithCause: (_, _, _, _, _) =>
                    throw new InvalidOperationException("Bound exhaustion must not reopen a Developer."),
                recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
                getLandingFileScopes: _ => ["src/Changed.cs"],
                executionDirectory: root,
                apparatusRedGate: CreateGate(root, index, perGoalRegateCap: 1));

            Assert.IsType<ConductorAdvanceOutcome.Held>(
                driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).Outcome);
            Assert.Equal(1, AcceptanceFailingTestIndex.CountRegates(index.Read(), goal.Id.Value));
            var exhausted = Assert.IsType<ConductorAdvanceOutcome.Escalated>(
                driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).Outcome);

            Assert.Contains(ApparatusRedClassifier.BoundExhaustedToken, exhausted.Reason, StringComparison.Ordinal);
            Assert.Contains(AcceptanceWithinAttemptRerunEvidence.Reason, exhausted.Reason, StringComparison.Ordinal);
            Assert.Contains(ApparatusRedClassifier.CandidateRerunEvidenceKind, exhausted.Reason, StringComparison.Ordinal);
            Assert.Equal(1, AcceptanceFailingTestIndex.CountRegates(index.Read(), goal.Id.Value));
            Assert.Equal(WorkTaskStatus.Completed, task.Status);
            Assert.Equal(0, goal.AutomaticAcceptanceRetryCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertRetriesDeveloper(Variant variant)
    {
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            WriteTestSource(root);
            var index = CreateIndex(root);
            var (kernel, goal) = SimpleGoal();
            var task = goal.Tasks.Single();
            PassVerification(kernel, goal, task);
            var checks = MixedChecks();
            if (variant == Variant.FailedCandidateRerun)
            {
                checks[1] = checks[1] with
                {
                    FailingTestAttributions =
                    [new AcceptanceTestFailureAttribution(
                        Identity, AcceptanceTestFailureOrigin.Introduced,
                        "focused identity was green at merge-base",
                        new CandidateFailureRerunEvidence("Failed", "receipt:candidate-rerun"))]
                };
            }
            else if (variant == Variant.MissingWithinAttemptEvidence)
            {
                checks[0] = checks[0] with { WithinAttemptRerun = null };
            }
            else if (variant == Variant.ZeroExecutedRerunTests)
            {
                checks[0] = checks[0] with
                {
                    WithinAttemptRerun = checks[0].WithinAttemptRerun! with { ExecutedTestCount = 0 }
                };
            }

            var driver = MakeDriver(
                getFacts: _ => GoalLifecycleFacts.None,
                runAcceptanceSummary: _ => RedSummary(checks),
                retryTaskWithCause: (goalId, taskId, message, roundKind, cause) =>
                    kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind, retryCause: cause),
                recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
                getLandingFileScopes: _ => variant == Variant.InsideChangedPaths
                    ? [SourcePath] : ["src/Changed.cs"],
                executionDirectory: root,
                apparatusRedGate: CreateGate(root, index));

            var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
            Assert.Equal(WorkTaskStatus.Assigned, task.Status);
            Assert.Equal(1, task.CriterionRetryCount);
            Assert.Equal(0, AcceptanceFailingTestIndex.CountRegates(index.Read(), goal.Id.Value));
            var genuine = Assert.Single(GoalOperationJournal.Read(root, goal.Id).Entries.Where(entry =>
                entry.Operation == GoalOperationJournal.AcceptanceApparatusGenuineOperation));
            var expectedReason = variant switch
            {
                Variant.FailedCandidateRerun => $"failing test {Identity} failed again on the candidate",
                Variant.InsideChangedPaths => $"failing test {Identity} lives inside the candidate's changed paths",
                _ => "a failing check carries no test identity, so there is nothing to locate outside the candidate"
            };
            Assert.Equal(expectedReason, genuine.Detail);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static AcceptanceCheckResult[] MixedChecks()
    {
        var crashed = new AcceptanceCheckResult(
            CrashedName, false, 0, "first run crashed without TRX",
            FailureClassification: AcceptanceShardCompletionPredicates.MissingTrx,
            CompletionDecision: new AcceptanceShardCompletionDecision(
                false, AcceptanceShardCompletionPredicates.MissingTrx, false, 0, 137, 0, "missing"))
        {
            WithinAttemptRerun = new AcceptanceWithinAttemptRerunEvidence(
                "Goal acceptance verifier", AcceptanceShardCompletionPredicates.MissingTrx,
                "attempt:0", "attempt:1", 137, ["goal-acceptance-verifier-run-1.trx"])
        };
        var failed = new AcceptanceCheckResult(
            FailedName, false, 1, "Assert.Equal() Failure",
            FailingTestIdentities: [Identity],
            TestProjectPath: "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
            FailingTestAttributions:
            [new AcceptanceTestFailureAttribution(
                Identity, AcceptanceTestFailureOrigin.UnconfirmedIntroduced,
                "focused identity was green at merge-base",
                new CandidateFailureRerunEvidence("Passed", "receipt:candidate-rerun"))]);
        var aggregate = new AcceptanceCheckResult(
            "infrastructure tests", false, 1, "aggregate failed",
            CoveredBy: [crashed.Name, failed.Name]);
        return [crashed, failed, aggregate];
    }

    private static AcceptanceVerificationSummary RedSummary(params AcceptanceCheckResult[] checks) => new(
        false, checks,
        FailedChecks: checks.Select(check => check.Name).ToArray(),
        BranchHeadSha: "candidate-a", MainHeadSha: "main-a",
        CheckAttributions: checks.Select(check => new AcceptanceCheckAttribution(
            check.Name, AcceptanceFailureOrigin.Introduced, "main green")).ToArray());

    private static AcceptanceFailingTestIndex CreateIndex(string root) => new(
        Path.Combine(root, "acceptance-gate-attempts", AcceptanceFailingTestIndex.FileName));

    private static ApparatusRedGate CreateGate(
        string root, AcceptanceFailingTestIndex index, int perGoalRegateCap = ApparatusRedGate.DefaultPerGoalRegateCap) =>
        new(index, _ => root, () => DateTimeOffset.Parse("2026-09-27T01:15:00Z"),
            perGoalRegateCap: perGoalRegateCap);

    private static void WriteTestSource(string root)
    {
        var path = Path.Combine(root, SourcePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path,
            "namespace Mcg.AgentOrchestrator.Infrastructure.Tests;" + Environment.NewLine +
            "public sealed class GoalWorktreeTestsRebaseMergeMaterialization { }" + Environment.NewLine);
    }

    private enum Variant
    {
        FailedCandidateRerun,
        InsideChangedPaths,
        MissingWithinAttemptEvidence,
        ZeroExecutedRerunTests
    }
}
