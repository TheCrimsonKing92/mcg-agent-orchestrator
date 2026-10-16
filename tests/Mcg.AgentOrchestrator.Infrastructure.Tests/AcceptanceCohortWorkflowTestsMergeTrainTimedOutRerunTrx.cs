using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Real git, store, and stable-slot boundaries use the workflow fixture's isolated roots.
public sealed class AcceptanceCohortWorkflowTestsMergeTrainTimedOutRerunTrx : AcceptanceCohortWorkflowTests
{
    private const string Lane = "infrastructure tests: Remainder";
    private static readonly string[] Failures =
    [
        "TaskCompletionBuildEvidencePersistenceTests.StaleWaitingSnapshot_PendingChildStored_PollsWithoutAnotherBuild",
        "CompletionBuildEvidenceIdentityTests.InvalidTerminalReceiptCannotResumeCompletion"
    ];

    [Theory]
    [InlineData("written")]
    [InlineData("none")]
    [InlineData("missing")]
    public void RunTrain_TimedOutProbe_CapturesOnlyUsableRerunTrx(string rerun)
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        var cleanupContext = CreateIsolatedCleanupContext(repo);
        try
        {
            AddAcceptanceManifest(repo);
            var kernel = new AgentOrchestratorKernel();
            var first = CreateCompletedGoal(kernel, "First timeout train member", repo);
            var second = CreateCompletedGoal(kernel, "Second timeout train member", repo);
            var third = CreateCompletedGoal(kernel, "Third timeout train member", repo);
            foreach (var goal in new[] { first, second, third })
                kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
                    ["The full acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
                { AcceptanceGateOwnedAcceptanceCriteria = ["The full acceptance gate passes"] });
            _ = CreateWorktreeCandidate(repo, first.Id,
                "tests/Mcg.AgentOrchestrator.Core.Tests/TimeoutFirst.cs", "first");
            _ = CreateWorktreeCandidate(repo, second.Id,
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/TimeoutSecond.cs", "second");
            _ = CreateWorktreeCandidate(repo, third.Id,
                "tests/Mcg.AgentOrchestrator.Dashboard.Tests/TimeoutThird.cs", "third");

            var missingFirst = Path.Combine(repo, "remainder.trx");
            var rerunPath = Path.Combine(repo, "remainder-run-1.trx");
            if (rerun == "written") WriteIncidentTrx(rerunPath);
            var otherLanePath = WritePassingTrx(repo, "other-lane.trx");
            var check = new AcceptanceCheckResult(Lane, false, -1, "first run timed out",
                TestResultPaths: [missingFirst], FailingTestIdentities: [],
                FailureClassification: AcceptanceShardCompletionPredicates.TimedOut,
                CompletionDecision: new AcceptanceShardCompletionDecision(false,
                    AcceptanceShardCompletionPredicates.TimedOut, true, -1, null, null, "missing"))
            {
                WithinAttemptRerun = rerun == "none" ? null : new AcceptanceWithinAttemptRerunEvidence(
                    "remainder", AcceptanceShardCompletionPredicates.TimedOut, "first-id", "retry-id",
                    9962, [rerunPath], RerunPassed: false, RerunFailingTestIdentities: Failures)
            };
            var verification = new AcceptanceVerificationResult(false, false, 1, "RED",
                Checks: [check, new AcceptanceCheckResult("other lane", true, 0, null, TestResultPaths: [otherLanePath])],
                TestResultPaths: [missingFirst, otherLanePath]);
            var verifier = new SequenceAcceptanceVerifier([verification]);
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var driver = new ConductorDriver(kernel, workspace, verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), cleanupHooks: cleanupContext.Hooks);
            var selection = ProjectTrainSelection(driver, first, second, third);

            var output = CaptureConsole(() =>
            {
                var result = driver.RunMergeTrain(selection, [first, second, third], ConductorAutonomyPolicy.Permissive);
                Assert.Equal(1, verifier.RunCount);
                var store = new MergeTrainAcceptanceStore(
                    Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db"));
                if (rerun == "written")
                {
                    var recorded = Assert.IsType<MergeTrainReceipt>(result.RecordedReceipt);
                    var stored = Assert.IsType<MergeTrainReceipt>(store.TryReadReceipt(recorded.Identity.Value));
                    Assert.Equal(MergeTrainGateOutcome.InfrastructureFailure, stored.Outcome);
                    Assert.False(stored.ValidForLanding);
                    Assert.Equal(2, stored.GateTestResultPaths.Count);
                    Assert.Contains(stored.GateTestResultPaths, path => path.EndsWith("remainder-run-1.trx", StringComparison.Ordinal));
                    Assert.Contains(stored.GateTestResultPaths, path => path.EndsWith("other-lane.trx", StringComparison.Ordinal));
                    Assert.All(stored.GateTestResultPaths, path => Assert.True(File.Exists(path), path));
                    var failures = MergeTrainRedAttribution.ReadFatalFailures(stored.GateTestResultPaths);
                    Assert.Equal(Failures.Order(StringComparer.Ordinal), failures.Select(failure => failure.TestName).Order(StringComparer.Ordinal));
                }
                else
                {
                    Assert.Null(result.Receipt);
                    Assert.Null(result.RecordedReceipt);
                    Assert.Contains("FileNotFoundException", result.Detail, StringComparison.Ordinal);
                    var evidenceDirectory = Assert.Single(Directory.GetDirectories(
                        Path.Combine(workspace.OrchestratorDirectory, "merge-train-evidence")));
                    Assert.Null(store.TryReadReceipt(Path.GetFileName(evidenceDirectory)));
                }
            });

            var notes = output.Split('\n').Where(line => line.StartsWith("GROUPED_RECEIPT_RERUN_TRX kind=train", StringComparison.Ordinal));
            if (rerun == "written")
            {
                var note = Assert.Single(notes);
                Assert.Contains($"lane=\"{Lane}\"", note, StringComparison.Ordinal);
                Assert.Contains("receipt=merge-train-receipt-", note, StringComparison.Ordinal);
                Assert.Contains("first_predicate=timed-out first_trx=none", note, StringComparison.Ordinal);
                Assert.Contains("rerun_trx=remainder-run-1.trx rerun_passed=false", note, StringComparison.Ordinal);
            }
            else Assert.Empty(notes);
        }
        finally { DeleteDirectory(repo); }
    }

    private static void WriteIncidentTrx(string path) => File.WriteAllText(path, $"""
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
          <Results>
            <UnitTestResult testId="1" testName="{Failures[0]}" outcome="Failed">
              <Output><ErrorInfo><Message>Expected stored pending child.</Message></ErrorInfo></Output>
            </UnitTestResult>
            <UnitTestResult testId="2" testName="{Failures[1]}" outcome="Failed">
              <Output><ErrorInfo><Message>Expected invalid receipt rejection.</Message></ErrorInfo></Output>
            </UnitTestResult>
          </Results>
          <ResultSummary outcome="Failed">
            <Counters total="9966" executed="9962" passed="9960" failed="2" notExecuted="4" />
          </ResultSummary>
        </TestRun>
        """);
}
