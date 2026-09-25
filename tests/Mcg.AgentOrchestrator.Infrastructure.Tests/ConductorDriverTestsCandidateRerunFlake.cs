using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

public sealed class ConductorDriverTestsCandidateRerunFlake
{
    private const string FlakyIdentity =
        "Sample.Tests.UnchangedTests.FailsOnce";
    private const string GenuineIdentity =
        "Sample.Tests.OtherTests.FailsAgain";
    private const string Project = "tests/Sample.Tests/Sample.Tests.csproj";

    [Fact]
    public void PassingRerunRegatesWithoutWorkerAndRecordsCrossGoalOccurrence()
    {
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            WriteSource(root, "UnchangedTests");
            var index = CreateIndex(root);
            var (kernel, goal) = SimpleGoal();
            var task = goal.Tasks.Single();
            PassVerification(kernel, goal, task);
            var retryCalled = false;
            var failed = FailingCheck(FlakyIdentity, AcceptanceTestFailureOrigin.UnconfirmedIntroduced, "Passed");
            var driver = MakeDriver(
                getFacts: _ => GoalLifecycleFacts.None,
                runAcceptanceSummary: _ => RedSummary(failed),
                retryTaskWithCause: (_, _, _, _, _) =>
                {
                    retryCalled = true;
                    throw new InvalidOperationException("A passed candidate rerun must not reopen a worker.");
                },
                recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
                getLandingFileScopes: _ => ["src/Changed.cs"],
                apparatusRedGate: CreateGate(root, index));

            var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
            Assert.Equal(GoalLifecycleState.Verified, held.State);
            Assert.Contains(ApparatusRedClassifier.CandidateRerunEvidenceKind, held.Reason, StringComparison.Ordinal);
            Assert.False(retryCalled);
            Assert.Equal(0, task.CriterionRetryCount);
            Assert.Equal(0, goal.AutomaticAcceptanceRetryCount);
            var occurrence = Assert.Single(index.Read().Where(record =>
                record.Kind == AcceptanceFailingTestIndexKinds.GateFailure));
            Assert.Equal(FlakyIdentity, occurrence.TestIdentity);
            Assert.Equal(ApparatusRedClassifier.CandidateRerunEvidenceKind, occurrence.EvidenceKind);
            Assert.Equal(AcceptanceFailingTestIndex.ComputeMessageFingerprint("candidate failure"), occurrence.MessageFingerprint);
            Assert.True(AcceptanceFailingTestIndex.HasCrossGoalOccurrence(
                index.Read(), "another-goal", FlakyIdentity, occurrence.MessageFingerprint,
                DateTimeOffset.Parse("2026-09-24T12:00:00Z"), TimeSpan.FromDays(14)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedFailureRoutesWorkerEvenWhenAnotherIdentityPassed(bool mixed)
    {
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            WriteSource(root, "UnchangedTests");
            WriteSource(root, "OtherTests");
            var index = CreateIndex(root);
            var (kernel, goal) = SimpleGoal();
            var task = goal.Tasks.Single();
            PassVerification(kernel, goal, task);
            string? retryMessage = null;
            var genuine = FailingCheck(GenuineIdentity, AcceptanceTestFailureOrigin.Introduced, "Failed");
            var checks = mixed
                ? new[] { FailingCheck(FlakyIdentity, AcceptanceTestFailureOrigin.UnconfirmedIntroduced, "Passed"), genuine }
                : new[] { genuine };
            var driver = MakeDriver(
                getFacts: _ => GoalLifecycleFacts.None,
                runAcceptanceSummary: _ => RedSummary(checks),
                retryTaskWithCause: (goalId, taskId, message, roundKind, cause) =>
                {
                    retryMessage = message;
                    return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind, retryCause: cause);
                },
                recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
                getLandingFileScopes: _ => ["src/Changed.cs"],
                apparatusRedGate: CreateGate(root, index));

            var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
            Assert.Equal(WorkTaskStatus.Assigned, task.Status);
            Assert.Equal(1, task.CriterionRetryCount);
            Assert.Contains(GenuineIdentity, retryMessage, StringComparison.Ordinal);
            if (mixed)
            {
                Assert.DoesNotContain(FlakyIdentity, retryMessage, StringComparison.Ordinal);
                Assert.Contains(index.Read(), record =>
                    record.TestIdentity == FlakyIdentity &&
                    record.EvidenceKind == ApparatusRedClassifier.CandidateRerunEvidenceKind);
                Assert.DoesNotContain(index.Read(), record =>
                    record.Kind == AcceptanceFailingTestIndexKinds.ApparatusRegate);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AllFlakyWithoutUsableApparatusDispositionTakesRetryPath(bool gatePresent)
    {
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var (kernel, goal) = SimpleGoal();
            var task = goal.Tasks.Single();
            PassVerification(kernel, goal, task);
            string? retryMessage = null;
            var driver = MakeDriver(
                getFacts: _ => GoalLifecycleFacts.None,
                runAcceptanceSummary: _ => RedSummary(
                    FailingCheck(FlakyIdentity, AcceptanceTestFailureOrigin.UnconfirmedIntroduced, "Passed")),
                retryTaskWithCause: (goalId, taskId, message, roundKind, cause) =>
                {
                    retryMessage = message;
                    return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind, retryCause: cause);
                },
                recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
                getLandingFileScopes: _ => ["src/Changed.cs"],
                apparatusRedGate: gatePresent ? CreateGate(root, CreateIndex(root)) : null);

            var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
            Assert.Equal(WorkTaskStatus.Assigned, task.Status);
            Assert.Contains(FlakyIdentity, retryMessage, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PassingRerunUsesExistingRegateCap()
    {
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            WriteSource(root, "UnchangedTests");
            var index = CreateIndex(root);
            var now = DateTimeOffset.Parse("2026-09-24T12:00:00Z");
            var (kernel, goal) = SimpleGoal();
            var task = goal.Tasks.Single();
            PassVerification(kernel, goal, task);
            index.Append(
                [new AcceptanceFailingTestIndexRecord(
                    AcceptanceFailingTestIndex.ContractVersion,
                    AcceptanceFailingTestIndexKinds.ApparatusRegate,
                    goal.Id.Value,
                    now.AddMinutes(-1))],
                now);
            var driver = MakeDriver(
                getFacts: _ => GoalLifecycleFacts.None,
                runAcceptanceSummary: _ => RedSummary(
                    FailingCheck(FlakyIdentity, AcceptanceTestFailureOrigin.UnconfirmedIntroduced, "Passed")),
                retryTaskWithCause: (_, _, _, _, _) =>
                    throw new InvalidOperationException("A passed rerun must not reopen a worker."),
                recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
                getLandingFileScopes: _ => ["src/Changed.cs"],
                apparatusRedGate: new ApparatusRedGate(index, _ => root, () => now, perGoalRegateCap: 1));

            var outcome = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative).Outcome;

            var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(outcome);
            Assert.Contains(ApparatusRedClassifier.BoundExhaustedToken, escalated.Reason, StringComparison.Ordinal);
            Assert.Contains(ApparatusRedClassifier.CandidateRerunEvidenceKind, escalated.Reason, StringComparison.Ordinal);
            Assert.Equal(WorkTaskStatus.Completed, task.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static AcceptanceCheckResult FailingCheck(
        string identity,
        AcceptanceTestFailureOrigin origin,
        string rerunOutcome) => new(
        identity,
        Passed: false,
        ExitCode: 1,
        OutputTail: "candidate failure",
        FailingTestIdentities: [identity],
        TestProjectPath: Project,
        FailingTestAttributions:
        [new AcceptanceTestFailureAttribution(
            identity,
            origin,
            "focused identity was green at merge-base baseline",
            new CandidateFailureRerunEvidence(rerunOutcome, "candidate-rerun.trx"))]);

    private static AcceptanceVerificationSummary RedSummary(params AcceptanceCheckResult[] checks) => new(
        false,
        checks,
        FailedChecks: checks.Select(check => check.Name).ToArray(),
        BranchHeadSha: "candidate-a",
        MainHeadSha: "main-a",
        CheckAttributions: checks.Select(check => new AcceptanceCheckAttribution(
            check.Name, AcceptanceFailureOrigin.Introduced, "main green")).ToArray());

    private static AcceptanceFailingTestIndex CreateIndex(string root) => new(
        Path.Combine(root, "acceptance-gate-attempts", AcceptanceFailingTestIndex.FileName));

    private static ApparatusRedGate CreateGate(string root, AcceptanceFailingTestIndex index) => new(
        index, _ => root, () => DateTimeOffset.Parse("2026-09-24T12:00:00Z"));

    private static void WriteSource(string root, string className)
    {
        var directory = Path.Combine(root, "tests", "Sample.Tests");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, className + ".cs"),
            $"namespace Sample.Tests; public sealed class {className} {{ }}");
    }
}
