using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class BackgroundApparatusRegateStatusTests : ConductorBatchLoopTests
{
    private const string Identity = "Sample.Tests.UnchangedTests.FailsOnce";
    private const string Project = "tests/Sample.Tests/Sample.Tests.csproj";

    public BackgroundApparatusRegateStatusTests(ITestOutputHelper output) : base(output) { }

    [Fact]
    public void CandidateRerunPassRestoresVerifiedAndNextTickRunsGate()
    {
        var root = CreateSourceRoot();
        try
        {
            var (kernel, goal) = SimpleGoal();
            PassVerification(kernel, goal, goal.Tasks.Single());
            var attempts = 0;
            var escalations = new List<string>();
            var driver = MakeDriver(kernel, root, () => attempts++, escalations);
            var (run, attempt) = FailedBackgroundRun(goal, "Passed", AcceptanceTestFailureOrigin.UnconfirmedIntroduced);

            driver.BeginTick(kernel, 1);
            ConductorBatchLoop.ReconcileParallelAcceptanceTerminalState(kernel, goal, run, attempt);
            Assert.Equal(GoalStatus.AcceptanceFailed, goal.Status);
            var result = ConductorBatchLoop.CompleteParallelAcceptanceRun(
                driver, ConductorAutonomyPolicy.Conservative, run, attempt, out var leaseHeld);

            Assert.False(leaseHeld);
            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
            Assert.Equal(GoalLifecycleState.Verified, held.State);
            Assert.Contains(ApparatusRedClassifier.CandidateRerunEvidenceKind, held.Reason);
            Assert.Equal(GoalStatus.Verified, goal.Status);
            Assert.Null(goal.LatestAcceptanceFailure);
            Assert.Contains(goal.Timeline, item =>
                item.Kind == ProgressKind.GoalPolicyDecision &&
                item.Message.Contains("restored Verified for re-gate", StringComparison.Ordinal));

            driver.BeginTick(kernel, 2);
            var nextTick = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);
            Assert.Equal(1, attempts);
            Assert.False(nextTick.WasEscalated);
            Assert.DoesNotContain(escalations, reason =>
                reason.Contains("Unhandled lifecycle state AcceptanceFailed", StringComparison.Ordinal));
            Assert.DoesNotContain(goal.Timeline, item =>
                item.Message.Contains("Unhandled lifecycle state AcceptanceFailed", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AllInheritedRestoresVerifiedForExistingHoldRouting()
    {
        var root = CreateSourceRoot();
        try
        {
            var (kernel, goal) = SimpleGoal();
            PassVerification(kernel, goal, goal.Tasks.Single());
            var driver = MakeDriver(kernel, root);
            var (run, attempt) = FailedBackgroundRun(goal, null, AcceptanceTestFailureOrigin.Inherited);

            driver.BeginTick(kernel, 1);
            ConductorBatchLoop.ReconcileParallelAcceptanceTerminalState(kernel, goal, run, attempt);
            var result = ConductorBatchLoop.CompleteParallelAcceptanceRun(
                driver, ConductorAutonomyPolicy.Conservative, run, attempt, out _);

            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
            Assert.Equal(GoalLifecycleState.Verified, held.State);
            Assert.Contains("outside this goal's attributable scope", held.Reason);
            Assert.Equal(GoalStatus.Verified, goal.Status);
            Assert.Contains(goal.Timeline, item =>
                item.Kind == ProgressKind.GoalPolicyDecision &&
                item.Message.Contains("classified as Inherited", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void GenuineRerunFailureRetainsAcceptanceFailedAndUsesRetryPath()
    {
        var root = CreateSourceRoot();
        try
        {
            var (kernel, goal) = SimpleGoal();
            PassVerification(kernel, goal, goal.Tasks.Single());
            var retried = false;
            var driver = MakeDriver(kernel, root, retry: () =>
            {
                retried = true;
                throw new InvalidOperationException("retry path observed");
            });
            var (run, attempt) = FailedBackgroundRun(goal, "Failed", AcceptanceTestFailureOrigin.Introduced);

            driver.BeginTick(kernel, 1);
            ConductorBatchLoop.ReconcileParallelAcceptanceTerminalState(kernel, goal, run, attempt);
            ConductorBatchLoop.CompleteParallelAcceptanceRun(
                driver, ConductorAutonomyPolicy.Conservative, run, attempt, out _);

            Assert.True(retried);
            Assert.Equal(GoalStatus.AcceptanceFailed, goal.Status);
            Assert.DoesNotContain(goal.Timeline, item =>
                item.Kind == ProgressKind.GoalPolicyDecision &&
                item.Message.Contains("restored Verified", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExhaustedRegateBoundEscalatesWithoutRestoringVerified()
    {
        var root = CreateSourceRoot();
        try
        {
            var (kernel, goal) = SimpleGoal();
            PassVerification(kernel, goal, goal.Tasks.Single());
            var driver = MakeDriver(kernel, root, regateCap: 0);
            var (run, attempt) = FailedBackgroundRun(goal, "Passed", AcceptanceTestFailureOrigin.UnconfirmedIntroduced);

            driver.BeginTick(kernel, 1);
            ConductorBatchLoop.ReconcileParallelAcceptanceTerminalState(kernel, goal, run, attempt);
            var result = ConductorBatchLoop.CompleteParallelAcceptanceRun(
                driver, ConductorAutonomyPolicy.Conservative, run, attempt, out _);

            var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
            Assert.Contains(ApparatusRedClassifier.BoundExhaustedToken, escalated.Reason);
            Assert.Equal(GoalStatus.AcceptanceFailed, goal.Status);
            Assert.DoesNotContain(goal.Timeline, item =>
                item.Kind == ProgressKind.GoalPolicyDecision &&
                item.Message.Contains("restored Verified", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ConductorDriver MakeDriver(
        AgentOrchestratorKernel kernel,
        string root,
        Action? gateStarted = null,
        List<string>? escalations = null,
        Action? retry = null,
        int regateCap = 2) =>
        ConductorDriverTests.MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ =>
            {
                gateStarted?.Invoke();
                return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
            },
            retryTaskWithCause: (_, _, _, _, _) =>
            {
                retry?.Invoke();
                throw new InvalidOperationException("Unexpected worker retry");
            },
            recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
            recordAcceptanceFailure: (goal, checks, branch, main, attributions, baseline) =>
                kernel.RecordAcceptanceFailure(goal.Id, checks, branch, main, attributions, baseline),
            writeEscalation: (_, _, reason) => escalations?.Add(reason),
            getLandingFileScopes: _ => ["src/Changed.cs"],
            apparatusRedGate: new ApparatusRedGate(
                Path.Combine(root, "acceptance-index.jsonl"), _ => root,
                perGoalRegateCap: regateCap));

    private static (ConductorParallelAcceptanceRunResult Run, ConductorParallelAcceptanceAttempt Attempt)
        FailedBackgroundRun(Goal goal, string? rerunOutcome, AcceptanceTestFailureOrigin origin)
    {
        var check = new AcceptanceCheckResult(
            Identity,
            Passed: false,
            ExitCode: 1,
            OutputTail: "candidate failure",
            FailingTestIdentities: [Identity],
            TestProjectPath: Project,
            FailingTestAttributions:
            [new AcceptanceTestFailureAttribution(
                Identity, origin, "merge-base attribution",
                rerunOutcome is null ? null : new CandidateFailureRerunEvidence(rerunOutcome, "candidate-rerun.trx"))]);
        var summary = new AcceptanceVerificationSummary(
            false, [check], FailedChecks: [check.Name],
            BranchHeadSha: "candidate-a", MainHeadSha: "main-a",
            CheckAttributions:
            [new AcceptanceCheckAttribution(check.Name, AcceptanceFailureOrigin.Introduced, "main green")]);
        var candidate = ConductorParallelAcceptanceCandidate.Create(
            goal, 0, ["src/Changed.cs"], "candidate-a", "main-a");
        var run = ConductorParallelAcceptanceRunResult.Accepted(candidate, summary);
        var attempt = new ConductorParallelAcceptanceAttempt(
            "attempt-a", goal.Id.Value, goal.Id.Value[..8], 0,
            "candidate-a", "main-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            1234, ConductorParallelAcceptanceAttemptOutcome.Failed,
            "stdout", "stderr", "exit", "heartbeat", "result", "metadata");
        return (run, attempt);
    }

    private static string CreateSourceRoot()
    {
        var root = ConductorDriverTests.CreateTempDirectory();
        var directory = Path.Combine(root, "tests", "Sample.Tests");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "UnchangedTests.cs"),
            "namespace Sample.Tests; public sealed class UnchangedTests { }");
        return root;
    }
}
