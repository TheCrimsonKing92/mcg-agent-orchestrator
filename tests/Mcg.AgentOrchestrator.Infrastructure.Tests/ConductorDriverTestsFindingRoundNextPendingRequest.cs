using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsFindingRoundNextPendingRequest
{
    private const string FirstRequest = "Infrastructure.Tests:ConductorDriverTests";
    private const string SecondRequest = "Core.Tests:GoalLifecycleTests";

    [Fact]
    public void FirstPassingRequestAdvancesToNextDistinctRequest()
    {
        using var scenario = new FindingRoundScenario();

        var first = scenario.Advance();

        Assert.Equal([(scenario.CandidateSha, FirstRequest)], scenario.Runs);
        Assert.Contains("another distinct request",
            Assert.IsType<ConductorAdvanceOutcome.Held>(first.Outcome).Reason, StringComparison.Ordinal);
        var receipt = Assert.Single(scenario.Receipts);
        Assert.True(receipt.Accepted && receipt.Passed);
        Assert.False(File.Exists(scenario.TrxPath));

        // Recreate the driver: only the recorded receipt can advance this round.
        scenario.RestartDriver();
        for (var tick = 0; tick < 6 && scenario.Retried.Count == 0; tick++)
            scenario.Advance();

        Assert.Equal(
            [(scenario.CandidateSha, FirstRequest), (scenario.CandidateSha, SecondRequest)], scenario.Runs);
        Assert.Single(scenario.Runs.Where(run => run.Request == FirstRequest));
        Assert.Single(scenario.Runs.Where(run => run.Request == SecondRequest));
        Assert.Equal([scenario.Reviewer.Id], scenario.Retried);
    }

    [Fact]
    public void FailedFirstRequestRemainsEligibleOnSameCandidate()
    {
        using var scenario = new FindingRoundScenario(passed: false);

        scenario.Advance();
        var receipt = Assert.Single(scenario.Receipts);
        Assert.True(receipt.Accepted);
        Assert.False(receipt.Passed);
        scenario.Advance();

        Assert.Equal(
            [(scenario.CandidateSha, FirstRequest), (scenario.CandidateSha, FirstRequest)], scenario.Runs);
        Assert.Empty(scenario.Retried);

        scenario.Advance();
        Assert.Equal([FirstRequest, FirstRequest, FirstRequest], scenario.Runs.Select(run => run.Request));
        Assert.False(Assert.Single(scenario.Receipts).Passed);
        Assert.Empty(scenario.Retried);
    }

    [Fact]
    public void InconclusiveFirstRequestRemainsEligibleOnSameCandidate()
    {
        using var scenario = new FindingRoundScenario(outcomeReason: FindingEvidenceOutcomeReason.VacuousEvidence);

        scenario.Advance();
        var receipt = Assert.Single(scenario.Receipts);
        Assert.True(receipt.Accepted);
        Assert.False(receipt.Passed);
        scenario.Advance();

        Assert.Equal(
            [(scenario.CandidateSha, FirstRequest), (scenario.CandidateSha, FirstRequest)], scenario.Runs);
        Assert.Empty(scenario.Retried);

        scenario.Advance();
        Assert.Equal([FirstRequest, FirstRequest, FirstRequest], scenario.Runs.Select(run => run.Request));
        Assert.False(Assert.Single(scenario.Receipts).Passed);
        Assert.Empty(scenario.Retried);
    }

    [Fact]
    public void PassingFirstRequestRunsAgainOnNewCandidate()
    {
        using var scenario = new FindingRoundScenario();

        scenario.Advance();
        var originalReceipt = Assert.Single(scenario.Receipts);
        Assert.True(originalReceipt.Accepted && originalReceipt.Passed);
        scenario.CandidateSha = "def5678";
        scenario.Advance();

        Assert.Equal([("abc1234", FirstRequest), ("def5678", FirstRequest)], scenario.Runs);
        Assert.Contains(scenario.Receipts, receipt =>
            receipt.CandidateSha == "def5678" && receipt.Accepted && receipt.Passed);
        Assert.Empty(scenario.Retried);

        scenario.Advance();
        Assert.Equal(
            [("abc1234", FirstRequest), ("def5678", FirstRequest), ("def5678", SecondRequest)], scenario.Runs);
        Assert.Equal([scenario.Reviewer.Id], scenario.Retried);
    }

    private sealed class FindingRoundScenario : IDisposable
    {
        private readonly string _root = ConductorDriverTests.CreateTempDirectory();
        private readonly AgentOrchestratorKernel _kernel;
        private readonly bool _passed;
        private readonly FindingEvidenceOutcomeReason _outcomeReason;
        private ConductorDriver _driver = null!;

        public FindingRoundScenario(
            bool passed = true,
            FindingEvidenceOutcomeReason outcomeReason = FindingEvidenceOutcomeReason.ValidEvidence)
        {
            _passed = passed;
            _outcomeReason = outcomeReason;
            var created = SoftwareGoal();
            _kernel = created.Kernel;
            Goal = created.Goal;
            Reviewer = Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            foreach (var task in Goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
                PassVerification(_kernel, Goal, task);
            FailReviewerNeedsWork(_kernel, Goal, Reviewer, "Two distinct focused requests need evidence", findings:
            [
                EvidenceFindingWithRequest("First focused request", "first", classes: ["ConductorDriverTests"]),
                EvidenceFindingWithRequest("Second focused request", "second", project: "Core.Tests",
                    classes: ["GoalLifecycleTests"])
            ]);
            RestartDriver();
        }

        public Goal Goal { get; }
        public TaskSpec Reviewer { get; }
        public string CandidateSha { get; set; } = "abc1234";
        public string TrxPath => Path.Combine(_root, "missing-result.trx");
        public List<(string CandidateSha, string Request)> Runs { get; } = [];
        public List<TaskId> Retried { get; } = [];
        public IEnumerable<FindingEvidenceReceipt> Receipts => Reviewer.VerificationHistory
            .SelectMany(verification => verification.FindingEvidenceReceipts ?? []);

        public ConductorAdvanceResult Advance() => _driver.AdvanceOnce(Goal, ConductorAutonomyPolicy.Permissive);

        public void RestartDriver() => _driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(CandidateSha),
            focusedEvidenceAttemptCoordinator: new ConductorParallelAcceptanceAttemptCoordinator(
                _root, runInline: true, acquireStableSlotLease: (_, _) => null),
            runFocusedEvidence: (_, request) =>
            {
                // Keep the configured outcome across retries: the receipt store retains
                // the first receipt for each id, so a later pass cannot replace it.
                var passed = _passed;
                var outcomeReason = _outcomeReason;
                Runs.Add((CandidateSha, request));
                var check = new AcceptanceCheckResult(
                    "candidate focused evidence", passed, passed ? 0 : 1, "Executed: 1",
                    TestResultPaths: [TrxPath], ExecutedTestCount: 1);
                // A recorded pass without retained TRX reproduces the strict-reuse rejection.
                // No candidate RED arm: failure tests exercise reselection, not RED attribution.
                return new FocusedEvidenceRunResult(
                    request, Accepted: true, Passed: passed, "focused evidence completed", [check],
                    Arms: passed
                        ? [new FocusedEvidenceArmRunResult(
                            FindingEvidenceArm.Candidate, CandidateSha, FindingEvidenceArmDisposition.Green,
                            Accepted: true, Passed: true, "candidate passed", [check])]
                        : [],
                    OutcomeReason: outcomeReason);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                Retried.Add(taskId);
                return _kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                _kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                _kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
