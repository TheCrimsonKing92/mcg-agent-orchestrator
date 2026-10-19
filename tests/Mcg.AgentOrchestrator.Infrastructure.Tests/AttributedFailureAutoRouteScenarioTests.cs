using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Fixture = RepositoryTestReachTests.RepositoryFixture;
using static ConductorDriverTests;
using static AttributedFailureDeveloperRouteTests;

// Parallel-safe: each train owns its repository, worktrees, database and cleanup hooks.
public sealed class AttributedFailureAutoRouteScenarioTests : AcceptanceCohortWorkflowTests
{
    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void CohortRoutesCulpritOnceAcrossThreeTicks()
    {
        var (kernel, first, second) = CohortGoals();
        ApplyCohort(kernel, first, second);
        var harness = new RouteHarness(kernel);
        var driver = harness.Configure();
        for (var tick = 1; tick <= 3; tick++)
        {
            driver.BeginTick(kernel, tick);
            driver.AdvanceOnce(second, ConductorAutonomyPolicy.Permissive);
        }
        AssertRoute(second, second.Tasks.Single(), harness, "partition-1", Candidate, CohortTests);
        Assert.Empty(MarkerRetries(first));
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    [InlineData(true)]
    [InlineData(false)]
    public void TrainRoutesOwnershipOrReachCulprit(bool ownership)
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        var cleanup = CreateIsolatedCleanupContext(repo);
        try
        {
            AddAcceptanceManifest(repo);
            Fixture.Populate(repo);
            Fixture.WriteAt(repo, Fixture.Intermediate, "public class Intermediate { private ChangedSource value; }");
            var names = new[] { "AlphaReachTests", "ZebraReachTests" };
            var failureDirectory = Fixture.TestDirectory + "/Unchanged/";
            foreach (var name in names) Fixture.WriteAt(repo, failureDirectory + name + ".cs", TestSource(name));
            RunGit(repo, "add", "src", "tests");
            RunGit(repo, "commit", "-m", "Add source reach fixture");
            var kernel = new AgentOrchestratorKernel();
            var goals = Enumerable.Range(0, 3).Select(index =>
            {
                var goal = CompletedGoal(kernel, repo);
                kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
                    ["The full acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
                { AcceptanceGateOwnedAcceptanceCriteria = ["The full acceptance gate passes"] });
                if (index == 0)
                {
                    CreateWorktreeCandidate(repo, goal.Id, Fixture.Source, "public class ChangedSource { public bool Changed => true; }");
                    CreateWorktreeCandidate(repo, goal.Id, Fixture.TestDirectory + "/Evidence/MemberEvidenceTests.cs",
                        "public class MemberEvidenceTests { [Xunit.Fact] public void Runs() { } }");
                }
                else if (index == 1)
                {
                    CreateWorktreeCandidate(repo, goal.Id, Fixture.Other, "public class OtherSource { public bool Changed => true; }");
                    CreateWorktreeCandidate(repo, goal.Id, Fixture.TestDirectory + "/Evidence/PartnerEvidenceTests.cs",
                        "public class PartnerEvidenceTests { [Xunit.Fact] public void Runs() { } }");
                    if (ownership)
                        foreach (var name in names)
                            CreateWorktreeCandidate(repo, goal.Id, failureDirectory + name + ".cs", TestSource(name) + " // owned");
                }
                else CreateWorktreeCandidate(repo, goal.Id, Fixture.TestDirectory + "/Cli/NewestMemberTests.cs",
                    "public class NewestMemberTests { [Xunit.Fact] public void Runs() { } }");
                return goal;
            }).ToArray();
            var paths = MergeTrainReachAttributionTests.Trx(repo, "ZebraReachTests.Fails", "AlphaReachTests.Fails");
            var verifier = new SequenceAcceptanceVerifier(
            [
                new(false, false, 1, "train failed", Checks: [new("train", false, 1, "train failed")], TestResultPaths: paths),
                new(true, false, 0, null, Checks: [new("remainder", true, 0, null)], TestResultPaths: [WritePassingTrx(repo, "pass.trx")])
            ]);
            var trainDriver = new ConductorDriver(kernel, OrchestratorWorkspace.ForDirectory(repo), verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), cleanupHooks: cleanup.Hooks);
            var selection = ProjectTrainSelection(trainDriver, goals);
            trainDriver.RunMergeTrain(selection, goals, ConductorAutonomyPolicy.Permissive, gateOnly: true);
            var culpritIndex = ownership ? 1 : 0;
            var culprit = goals[culpritIndex];
            Assert.Equal(GoalStatus.AcceptanceFailed, culprit.Status);
            var candidate = selection.Members[culpritIndex].CandidateRevision;
            var receipt = new MergeTrainAcceptanceStore(Path.Combine(OrchestratorWorkspace.ForDirectory(repo).OrchestratorDirectory,
                "merge-train-acceptance.db")).ReadReceiptsSince(DateTimeOffset.MinValue).Single(item => item.Outcome == MergeTrainGateOutcome.Failed);
            var harness = new RouteHarness(kernel, candidate);
            var driver = harness.Configure();
            for (var tick = 1; tick <= 2; tick++)
            {
                driver.BeginTick(kernel, tick);
                driver.AdvanceOnce(culprit, ConductorAutonomyPolicy.Permissive);
            }
            AssertRoute(culprit, culprit.Tasks.Single(), harness, receipt.ReceiptId, candidate, names);
            Assert.Contains("source=train-" + (ownership ? "ownership" : "reach"), MarkerRetries(culprit).Single().Message);
            Assert.All(goals.Where(goal => goal != culprit), goal => Assert.Empty(MarkerRetries(goal)));
        }
        finally { DeleteDirectory(repo); }
    }

    private static string TestSource(string name) =>
        $"public class {name} {{ private Intermediate value; [Xunit.Fact] public void Fails() {{ }} }}";

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void TesterReceiptReturnsToTesterThenRoutesDeveloper()
    {
        var (kernel, goal, tester, receipt) = DeliverTesterReceipt();
        // The Tester reports the delivered RED without requesting another evidence run.
        // The kernel retains the open finding and its receipt in merged history.
        RecordTesterResult(kernel, goal, tester, fail: true, reportFinding: false);
        Assert.True(WorkerResultBlockers.TryFindReviewFindingRound(tester.LastVerification, out var round, out _));
        Assert.Empty(round.Findings);
        Assert.Equal(WorkTaskStatus.Failed, tester.Status);
        Assert.Contains(goal.Timeline, item => item.Message.Contains("automatic ownership routing required", StringComparison.Ordinal));
        Assert.Equal(receipt.ReceiptId, Assert.IsType<AttributedFailure>(AttributedFailureDeveloperRoute.ReadTesterReceipt(goal, tester, Candidate)).ReceiptId);
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var harness = new RouteHarness(kernel);
        var driver = harness.Configure();
        driver.BeginTick(kernel, 2);
        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        AssertRoute(goal, developer, harness, receipt.ReceiptId, Candidate, [TesterIdentity]);
        Assert.False(result.WasEscalated);
    }

    internal const string TesterIdentity = "ConductorDriverTests.ReproducedFailure";

    internal static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Tester, FindingEvidenceReceipt Receipt) DeliverTesterReceipt()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        PassVerification(kernel, goal, developer, hasCommittedChanges: true);
        kernel.RecordDispatchBaseCommit(goal.Id, developer.Id, Main);
        kernel.RecordDispatchResultCommit(goal.Id, developer.Id, Candidate);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        RecordTesterResult(kernel, goal, tester, fail: false);
        var harness = new RouteHarness(kernel);
        var focusedRuns = 0;
        var driver = harness.Configure((_, request) =>
        {
            focusedRuns++;
            return CandidateRedFindingEvidence(request, Candidate, TesterIdentity);
        });
        driver.BeginTick(kernel, 1);
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        Assert.Equal(1, focusedRuns);
        Assert.Equal(tester.Id, Assert.Single(harness.Dispatches));
        Assert.Empty(MarkerRetries(goal));
        var receipt = tester.VerificationHistory.SelectMany(record => record.FindingEvidenceReceipts ?? []).Last();
        Assert.True(receipt.Accepted);
        var red = Assert.Single(receipt.Arms!, arm => arm.Arm == FindingEvidenceArm.Candidate);
        Assert.Equal(FindingEvidenceArmDisposition.Red, red.Disposition);
        Assert.Equal(new[] { TesterIdentity }, red.FailingTestIdentities);
        Assert.Equal(receipt.ReceiptId, Assert.Single(tester.VerificationHistory.Last().MergedReviewFindings!).EvidenceOutcome!.ReceiptId);
        return (kernel, goal, tester, receipt);
    }

    internal static void RecordTesterResult(AgentOrchestratorKernel kernel, Goal goal, TaskSpec tester, bool fail,
        ReviewFinding? overrideFinding = null, bool reportFinding = true)
    {
        // A TestEvidence finding about documentation is Tester-owned under the existing ownership map.
        // Its GREEN baseline does not trigger the existing source-anchor Developer route.
        var finding = overrideFinding ?? (fail ? Assert.Single(tester.VerificationHistory.Last().MergedReviewFindings!) : new ReviewFinding(
            "receipt-bound-test", ReviewFindingState.Open, new("docs/receipt-evidence.md", "ReceiptEvidence.Contract"),
            "Reproduce the documented schema expectation at this candidate.", FindingSeverity.Blocking, FindingCategory.TestEvidence,
            new([new("Infrastructure.Tests", "ConductorDriverTests")])));
        var priorReceiptId = tester.VerificationHistory.SelectMany(record => record.MergedReviewFindings ?? [])
            .LastOrDefault(prior => prior.StableId == finding.StableId)?.EvidenceOutcome?.ReceiptId;
        if (tester.Status != WorkTaskStatus.Running) DispatchTask(kernel, goal, tester, "tester");
        CompleteRecordedProcess(kernel, goal, tester);
        // Every open blocking TestEvidence report must retain its evidence request.
        // The kernel preserves the Conductor-owned receipt when the same request is resubmitted.
        var reportedFinding = finding with { EvidenceOutcome = null };
        var stdout = string.Join(Environment.NewLine,
            "WORKER_RESULT:", "files: none", "commands: inspect focused behavior",
            fail ? "tests: fail - receipt reproduces the failure" : "tests: deferred - ConductorDriverTests",
            "commit: none", "blockers: none", $"findings: {JsonSerializer.Serialize(reportFinding ? new[] { reportedFinding } : [])}",
            "touched_anchors: []", "verdict: needs-work", "model_fit: fixture/model - adequate - source verification",
            "skills: none", "confidence: high", "END_WORKER_RESULT");
        var dispatch = tester.LastDispatch!;
        // Recovery binds the applied verification to the process's exact exit timestamp.
        var completedAt = tester.LastProcess?.CompletedAt ?? DateTimeOffset.UtcNow;
        var verification = new TaskVerificationRecord(dispatch.Command, dispatch.WorkingDirectory, 0, stdout, "", completedAt,
            StandardOutputPath: tester.LastProcess?.StandardOutputPath ?? Path.Combine(Path.GetTempPath(), "tester.out.log"),
            WorkerResultPresent: true, DispatchStartedAt: tester.LastProcess?.StartedAt, DispatchAttemptAt: dispatch.DispatchedAt);
        Assert.True(WorkerResultBlockers.TryFindReviewFindingRound(verification, out var round, out var diagnostic), diagnostic);
        if (reportFinding)
        {
            var reported = Assert.Single(round.Findings);
            Assert.Equal(finding.EvidenceRequest!.Selections.ToArray(), Assert.IsType<FindingEvidenceRequest>(reported.EvidenceRequest).Selections.ToArray());
            Assert.Null(reported.EvidenceOutcome);
        }
        else Assert.Empty(round.Findings);
        kernel.RecordDispatchExecutionResult(goal.Id, tester.Id, verification);
        if (tester.LastProcess is { } process)
            Assert.True(DispatchProcessCompletionState.HasAlreadyBeenApplied(tester, process),
                "The recorded Tester exit must have an applied completion before failure recovery.");
        Assert.Null(tester.LastVerification!.ReviewFindingContractViolation);
        var merged = Assert.Single(tester.LastVerification.MergedReviewFindings!);
        Assert.Equal(finding.StableId, merged.StableId);
        Assert.Equal(finding.EvidenceRequest!.Selections.ToArray(), merged.EvidenceRequest!.Selections.ToArray());
        Assert.Equal(priorReceiptId, merged.EvidenceOutcome?.ReceiptId);
        Assert.True(WorkerResultBlockers.TryGetTestsStatus(tester.LastVerification, out var status));
        Assert.Equal(fail ? WorkerResultBlockers.TestsStatus.Fail : WorkerResultBlockers.TestsStatus.Deferred, status);
    }

    internal static void CompleteRecordedProcess(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task)
    {
        if (task.LastProcess is { IsRunning: true } process)
            kernel.RecordTaskProcessRefreshed(goal.Id, task.Id, process with { CompletedAt = DateTimeOffset.UtcNow, ExitCode = 0 }, null);
    }

    internal static void AssertRoute(Goal goal, TaskSpec developer, RouteHarness harness,
        string receipt, string candidate, IReadOnlyList<string> identities)
    {
        var retry = Assert.Single(MarkerRetries(goal));
        Assert.Equal(developer.Id, retry.TaskId);
        Assert.Equal(RetryCause.NewTestFinding, developer.PendingRetryCause);
        Assert.Contains("receipt_id=" + receipt, retry.Message);
        Assert.Contains("candidate_sha=" + candidate, retry.Message);
        Assert.All(identities, test => Assert.Contains(test, retry.Message));
        Assert.Equal(developer.Id, Assert.Single(harness.Dispatches));
        Assert.DoesNotContain(harness.Escalations, reason => reason.Contains("Unhandled lifecycle state", StringComparison.Ordinal));
        AssertNoOperatorIntent(goal);
    }

    internal static void AssertNoOperatorIntent(Goal goal) => Assert.DoesNotContain(goal.Timeline,
        item => item.Message.Contains("OPERATOR_ADJUDICATION", StringComparison.Ordinal) ||
                item.Message.Contains("operator-intent:", StringComparison.Ordinal));
}
