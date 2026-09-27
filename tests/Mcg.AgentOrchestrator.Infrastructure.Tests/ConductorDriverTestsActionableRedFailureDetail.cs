using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsActionableRedFailureDetail
{
    private const string CandidateSha = "abc1234";
    private const string TestIdentity = "GateReadyCandidateProjectorTests.DefaultHarnessProducesSerializedResourceKey";

    [Fact]
    public void ActionableCandidateRedRetryIncludesTrxMessageAndStack()
    {
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var trxPath = Path.Combine(root, "candidate.trx");
            WriteFailureTrx(trxPath, TestIdentity);
            var (message, developer, retriedTask, starts, receiptId) = RouteCandidateRed(trxPath);

            Assert.Equal(developer, retriedTask);
            Assert.Equal(1, starts);
            Assert.StartsWith(ExpectedPrefix(receiptId), message, StringComparison.Ordinal);
            Assert.Contains("Candidate failure detail (receipt " + receiptId + "):", message, StringComparison.Ordinal);
            Assert.Contains("[FAIL] " + TestIdentity + " (Failed)", message, StringComparison.Ordinal);
            Assert.Contains("fatal: synthetic too big", message, StringComparison.Ordinal);
            Assert.Contains("at Synthetic.First()", message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ActionableCandidateRedRetryContinuesWhenTrxIsMissing()
    {
        var root = ConductorDriverTests.CreateTempDirectory();
        try
        {
            var trxPath = Path.Combine(root, "missing.trx");
            var (message, developer, retriedTask, starts, receiptId) = RouteCandidateRed(trxPath);

            Assert.Equal(developer, retriedTask);
            Assert.Equal(1, starts);
            Assert.StartsWith(ExpectedPrefix(receiptId), message, StringComparison.Ordinal);
            Assert.Contains("detail unavailable: no TRX exists at " + trxPath, message, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ReaderExceptionAddsUnavailableNoteWithoutChangingRetryPrefix()
    {
        const string prefix = "ACTIONABLE_CANDIDATE_RED original prefix";
        var result = ConductorDriver.AppendActionableCandidateRedFailureDetail(
            prefix, "receipt-1", [TestIdentity], ["candidate.trx"],
            _ => throw new InvalidOperationException("boom"));

        Assert.Equal(prefix + Environment.NewLine + Environment.NewLine +
                     "Candidate failure detail (receipt receipt-1):" + Environment.NewLine +
                     "detail unavailable: System.InvalidOperationException: boom", result);
    }

    private static (string Message, TaskId Developer, TaskId? RetriedTask, int Starts, string ReceiptId)
        RouteCandidateRed(string trxPath)
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
            PassVerification(kernel, goal, task, hasCommittedChanges: task == developer);
        FailReviewerNeedsWork(kernel, goal, reviewer, "candidate RED must be actionable",
            findings: [EvidenceFindingWithRequest("Developer-owned fixture is RED.",
                id: "fixture-red", classes: ["GateReadyCandidateProjectorTests"])]);

        TaskId? retriedTask = null;
        string? retryMessage = null;
        var starts = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(CandidateSha),
            runFocusedEvidence: (_, request) =>
            {
                var red = CandidateRedFindingEvidence(request, CandidateSha);
                var candidate = red.Arms!.Single(arm => arm.Arm == FindingEvidenceArm.Candidate);
                var check = candidate.Checks.Single() with { TestResultPaths = [trxPath] };
                return red with
                {
                    Checks = [check],
                    Arms = red.Arms.Select(arm => arm.Arm == FindingEvidenceArm.Candidate
                        ? arm with { Checks = [check] } : arm).ToArray()
                };
            },
            dispatchAndStart: _ => { starts++; return DispatchStartOutcome.Started(); },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTask = taskId;
                retryMessage = message;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceRun: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRun(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        var receiptId = Assert.Single(reviewer.VerificationHistory.Last().FindingEvidenceReceipts!).ReceiptId;
        return (Assert.IsType<string>(retryMessage), developer.Id, retriedTask, starts, receiptId);
    }

    private static string ExpectedPrefix(string receiptId) =>
        $"ACTIONABLE_CANDIDATE_RED candidate_sha={CandidateSha}; receipt_id={receiptId}; " +
        $"finding_ids=fixture-red; failing_tests={TestIdentity}.  " +
        "Repair the Developer-owned source/test anchor before any remaining focused evidence or downstream verification runs.";

    internal static void WriteFailureTrx(string path, string testName) =>
        File.WriteAllText(path, $"""
            <TestRun><Results><UnitTestResult testName="{testName}" outcome="Failed">
            <Output><ErrorInfo><Message>fatal: synthetic too big</Message>
            <StackTrace>at Synthetic.First()
            at Synthetic.Second()</StackTrace></ErrorInfo></Output>
            </UnitTestResult></Results></TestRun>
            """);
}
