using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

// Parallel-safe: scenario-owned kernel, clock, worktree and inline attempt coordinator.
public sealed class TesterRedLabelledInconclusiveScenarioTests
{
    private const string Candidate = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string OtherCandidate = "cccccccccccccccccccccccccccccccccccccccc";
    private const string HoldClass = "ConductorContinuitySupervisorDeliberateHoldProbeTests";
    private const string AdmissionClass = "ConductorContinuitySupervisorAdmissionProbeTests";
    private const string HoldPath = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/" + HoldClass + ".cs";
    private const string CorePath = "tests/Mcg.AgentOrchestrator.Core.Tests/DeclaredCoreTests.cs";
    private static readonly string[] IncidentFailures =
    [
        AdmissionClass + ".AdmissionLanding_ThreeTicks_WaitsForCompleteProbe",
        HoldClass + ".NoVerifiedGoal_RealLoop_EqualsLiteralZeroWaitingBaseline",
        HoldClass + ".GatherHold_SixMinutesOfRealTicks_AdoptsWithoutReverting",
        HoldClass + ".AdmissibleAttempt_LeaseBusyForSixMinutes_RevertsAndRestores"
    ];

    [Fact]
    public void RefreshedRedReceiptRoutesDeveloperAfterSecondInconclusiveRound()
    {
        using var scenario = new Scenario();
        var unusable = scenario.FirstUnusable();
        scenario.CompleteTester(HoldClass + " timed out without TRX");

        Assert.IsType<ConductorAdvanceOutcome.Executed>(scenario.Advance().Outcome);
        var red = scenario.Latest();
        Assert.Equal("red", red.Outcome);
        Assert.NotEqual(unusable.ReceiptId, red.ReceiptId);
        Assert.Equal(IncidentFailures, red.FailingTests);
        Assert.Contains("Infrastructure.Tests:" + HoldClass, red.Selections);
        Assert.Contains("Infrastructure.Tests:" + AdmissionClass, red.Selections);
        Assert.Equal(2, scenario.Runs);
        Assert.Equal(new[] { AgentRole.Tester, AgentRole.Tester }, scenario.DispatchedRoles);
        // Red pre-Tester receipts carry their artifact path in the index, without registering a coordinator receipt id.
        var artifact = Assert.IsType<ConductorParallelAcceptanceRunArtifact>(
            JsonSerializer.Deserialize<ConductorParallelAcceptanceRunArtifact>(
                File.ReadAllText(Assert.IsType<string>(red.ResultPath)), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal("focused-evidence", artifact.Kind);
        Assert.Equal(Candidate, artifact.BranchHeadSha);
        var receipt = Assert.IsType<FocusedEvidenceRunResult>(artifact.FocusedEvidence);
        Assert.True(receipt.Accepted);
        var arm = Assert.Single(receipt.Arms!);
        Assert.Equal(FindingEvidenceArm.Candidate, arm.Arm);
        Assert.Equal(FindingEvidenceArmDisposition.Red, arm.Disposition);
        Assert.True(arm.Accepted);
        Assert.Equal(3, arm.Checks.Count(check => check.OutputTail == "Test execution timed out after 300000 milliseconds"));
        Assert.Single(arm.Checks, check => check.OutputTail == "healthyTicks=6");
        scenario.CompleteTester("conductor TRX records all three deliberate-hold scenarios timing out after 300000ms; " +
            "separately, AdmissionLanding_ThreeTicks_WaitsForCompleteProbe fails its healthyTicks=6 assertion");
        var dispatches = scenario.DispatchedRoles.Count;

        Assert.IsType<ConductorAdvanceOutcome.Executed>(scenario.Advance().Outcome);

        var retry = Assert.Single(AttributedFailureDeveloperRouteTests.MarkerRetries(scenario.Goal));
        Assert.Equal(scenario.Developer.Id, retry.TaskId);
        Assert.StartsWith("ATTRIBUTED_FAILURE_ROUTE source=pre-tester-receipt", retry.Message);
        Assert.Contains("receipt_id=" + red.ReceiptId, retry.Message, StringComparison.Ordinal);
        Assert.Contains("candidate_sha=" + Candidate, retry.Message, StringComparison.Ordinal);
        Assert.All(IncidentFailures, identity => Assert.Contains(identity, retry.Message, StringComparison.Ordinal));
        Assert.Equal(RetryCause.NewTestFinding, scenario.Developer.PendingRetryCause);
        Assert.Equal(new[] { AgentRole.Developer }, scenario.DispatchedRoles.Skip(dispatches));
        Assert.DoesNotContain(scenario.Escalations, reason => reason.Contains("stayed verification-inconclusive", StringComparison.Ordinal));
        Assert.DoesNotContain(scenario.Goal.Timeline, evt =>
            evt.Message.Contains("OPERATOR_ADJUDICATION", StringComparison.Ordinal) ||
            evt.Message.Contains("operator-intent:", StringComparison.Ordinal));
    }

    [Fact]
    public void UnusableReceiptRetryEqualsDisabledRouteBaseline()
    {
        using var actual = new Scenario();
        using var baseline = new Scenario(baseline: true);
        foreach (var scenario in new[] { actual, baseline })
        {
            scenario.FirstUnusable();
            scenario.CompleteTester(HoldClass + " timed out without TRX");
        }

        AssertBaselineEquality(actual, baseline);
        Assert.Equal("red", actual.Latest().Outcome);
        Assert.Equal(2, actual.Runs);
        Assert.Equal(new[] { AgentRole.Tester }, actual.RetriedRoles());
    }

    [Theory]
    [InlineData("outside-selection")]
    [InlineData("not-run")]
    [InlineData("green")]
    [InlineData("different-candidate")]
    public void DeclinedReceiptRecoveryEqualsDisabledRouteBaseline(string control)
    {
        using var actual = new Scenario(incident: false);
        using var baseline = new Scenario(incident: false, baseline: true);
        foreach (var scenario in new[] { actual, baseline })
        {
            scenario.SeedUsable(control == "outside-selection" ? "red" : "green");
            Assert.IsType<ConductorAdvanceOutcome.Executed>(scenario.Advance().Outcome);
            if (control != "outside-selection")
                scenario.Record(control == "green" ? "green" : "red",
                    candidate: control == "different-candidate" ? OtherCandidate : Candidate,
                    notRun: control == "not-run" ? ["DeclaredCoreTests"] : [],
                    failures: control == "green" ? [] : ["DeclaredCoreTests.Fails"]);
            scenario.CompleteTester("inspected retained receipts");
            if (control == "outside-selection")
            {
                Assert.IsType<ConductorAdvanceOutcome.Executed>(scenario.Advance().Outcome);
                scenario.CompleteTester("inspected retained receipts");
                Assert.True(TesterInconclusiveRoundInputsReader.Read(scenario.Goal, scenario.Tester)!.InputsUnchanged);
            }
        }

        var result = AssertBaselineEquality(actual, baseline);
        Assert.Equal(0, actual.Runs);
        if (control == "outside-selection")
            Assert.Equal("verification-inconclusive-unchanged-inputs", actual.RecoveryEvidence(result));
    }

    private static ConductorAdvanceResult AssertBaselineEquality(Scenario actual, Scenario baseline)
    {
        var expectedResult = baseline.Advance();
        var actualResult = actual.Advance();
        Assert.Equal(expectedResult.Outcome.GetType(), actualResult.Outcome.GetType());
        Assert.Equal(baseline.RecoveryEvidence(expectedResult), actual.RecoveryEvidence(actualResult));
        Assert.Equal(baseline.Runs, actual.Runs);
        Assert.Equal(baseline.DispatchedRoles.Count, actual.DispatchedRoles.Count);
        Assert.Equal(baseline.DispatchedRoles, actual.DispatchedRoles);
        Assert.Equal(baseline.RetriedRoles(), actual.RetriedRoles());
        Assert.Empty(AttributedFailureDeveloperRouteTests.MarkerRetries(actual.Goal));
        Assert.Empty(AttributedFailureDeveloperRouteTests.MarkerRetries(baseline.Goal));
        return actualResult;
    }

    private sealed class Scenario : IDisposable
    {
        private readonly string _root = ConductorDriverTests.CreateTempDirectory();
        private readonly FixtureClock _clock = new();
        private readonly bool _incident;
        private readonly bool _baseline;
        public AgentOrchestratorKernel Kernel { get; }
        public Goal Goal { get; }
        public TaskSpec Developer { get; } = new(TaskId.New(), "Implement", AgentRole.Developer);
        public TaskSpec Tester { get; } = new(TaskId.New(), "Verify", AgentRole.Tester);
        public int Runs { get; private set; }
        public List<AgentRole> DispatchedRoles { get; } = [];
        public List<string> Escalations { get; } = [];
        private List<FailedGoalRecoveryDecision> Recoveries { get; } = [];

        public Scenario(bool incident = true, bool baseline = false)
        {
            _incident = incident;
            _baseline = baseline;
            Kernel = new(_clock);
            Kernel.ConfigureCandidateIdentityResolver(_ => new CandidateIdentity("patch", "base", "manifest"));
            Goal = Kernel.CreateGoal("Recover pre-Tester red labelled inconclusive", [Developer, Tester]);
            Kernel.ActivateGoal(Goal.Id, DefaultAgents());
            Kernel.RecordTaskDispatch(Goal.Id, Developer.Id, new("fixture", "implement", _root, _clock.Next(), BaseCommit: "aaa1111"));
            var output = string.Join('\n', "WORKER_RESULT:", "files: " + (_incident ? HoldPath : CorePath), "commands: none",
                "tests: deferred - " + (_incident ? HoldClass + ", " + AdmissionClass : "DeclaredCoreTests"),
                "commit: none", "blockers: none", "assigned_scope_complete: true",
                "model_fit: test/model - adequate - fixture", "skills: none", "confidence: high", "END_WORKER_RESULT");
            Kernel.RecordTaskVerification(Goal.Id, Developer.Id, new("implement", _root, 0, output, "", _clock.Next(),
                WorkerResultPresent: true, HasCommittedChanges: true));
            Assert.Equal(WorkTaskStatus.Completed, Developer.Status);
            var worktree = GoalWorktrees.WorktreePath(_root, Goal.Id);
            Directory.CreateDirectory(worktree);
            File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: fixture");
            if (_incident)
            {
                Declare(worktree, HoldClass, "Mcg.AgentOrchestrator.Infrastructure.Tests");
                Declare(worktree, AdmissionClass, "Mcg.AgentOrchestrator.Infrastructure.Tests");
            }
            else Declare(worktree, "DeclaredCoreTests", "Mcg.AgentOrchestrator.Core.Tests");
            foreach (var entry in PreTesterAlwaysRunGuardTestClasses.Entries)
                Declare(worktree, entry.TestClass, "Mcg.AgentOrchestrator.Infrastructure.Tests");
        }

        private static void Declare(string worktree, string name, string projectName)
        {
            var project = Path.Combine(worktree, "tests", projectName);
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, projectName + ".csproj"), "<Project />");
            File.WriteAllText(Path.Combine(project, name + ".cs"), "public class " + name + " {}");
        }

        public ConductorParallelAcceptanceAttemptCoordinator Coordinator() => new(Path.Combine(_root, "attempts"),
            TrunkBranchName.Default, runInline: true, acquireStableSlotLease: (_, _) => null);

        public PreTesterEvidenceEntry Latest() => Assert.IsType<PreTesterEvidenceEntry>(
            PreTesterEvidenceIndexLines.Latest(Goal, Tester.Id, Candidate));

        public PreTesterEvidenceEntry FirstUnusable()
        {
            Assert.IsType<ConductorAdvanceOutcome.Executed>(Advance().Outcome);
            Assert.Equal(1, Runs);
            Assert.Equal(new[] { AgentRole.Tester }, DispatchedRoles);
            var entry = Latest();
            Assert.Equal("unusable", entry.Outcome);
            Assert.True(PreTesterApparatusReceiptClassifier.IsApparatusUnusable(
                Coordinator().ReadFocusedReceiptEvidence(Goal.Id, entry.ReceiptId, Candidate)));
            return entry;
        }

        public void CompleteTester(string detail)
        {
            var output = $"WORKER_RESULT:\nfiles: none\ntests: inconclusive - {detail}\nblockers: none\nEND_WORKER_RESULT";
            var dispatch = Assert.IsType<TaskDispatchRecord>(Tester.LastDispatch);
            Kernel.RecordDispatchExecutionResult(Goal.Id, Tester.Id, new(dispatch.Command, dispatch.WorkingDirectory, 0, output, "", _clock.Next(),
                WorkerResultPresent: true, DispatchStartedAt: dispatch.DispatchedAt));
            Assert.Equal(DispatchOutcomeKind.VerificationInconclusive,
                DispatchFailureClassifier.Classify(Tester, Tester.LastVerification!).Kind);
            Assert.Equal(WorkTaskStatus.Failed, Tester.Status);
        }

        public void Record(string outcome, string candidate = Candidate, string[]? notRun = null,
            string[]? failures = null, string? resultPath = null) =>
            Kernel.RecordFindingEvidenceRun(Goal.Id, Tester.Id, PreTesterEvidenceIndexLines.FormatMarker(
                new(outcome, candidate, "seed-receipt", ["Core.Tests:DeclaredCoreTests"], notRun ?? [], resultPath, failures ?? [])));

        public void SeedUsable(string outcome)
        {
            const string request = "Core.Tests:DeclaredCoreTests";
            var evidence = outcome == "green"
                ? ConductorDriverTestsFindingEvidenceReuse.RetainedEvidenceWithExecutedClasses(_root, request, Candidate)
                : CandidateRedFindingEvidence(request, Candidate, "ExternalTests.Fails");
            if (outcome == "red") evidence = evidence with { Arms = evidence.Arms!.Select(arm =>
                arm with { Disposition = FindingEvidenceArmDisposition.Red,
                    Checks = arm.Checks.Select(check => check with { ExecutedTestCount = 1 }).ToArray() }).ToArray() };
            var coordinator = Coordinator();
            var run = coordinator.EvaluateFocusedEvidence(ConductorParallelAcceptanceCandidate.Create(Goal, 0, [], Candidate, null),
                ConductorAutonomyPolicy.Permissive, request, (_, _, _, _) => evidence, new("seed", "seed-batch", []));
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Completed, run.Kind);
            Assert.True(coordinator.RecordFocusedEvidenceRequestDispositions(run.Attempt, "seed", "seed-receipt", []));
            coordinator.MarkReconciled(run.Attempt);
            Record(outcome, failures: outcome == "red" ? ["ExternalTests.Fails"] : [], resultPath: run.Attempt.ResultPath);
        }

        private static FocusedEvidenceRunResult Unusable(string request)
        {
            var check = new AcceptanceCheckResult("build", false, -1, "no tests executed",
                FailureClassification: AcceptanceFailureClassifications.BuildPhaseTimeout, ExecutedTestCount: 0);
            return new(request, true, false, "apparatus timeout", [check],
                Arms: [new(FindingEvidenceArm.Candidate, Candidate, FindingEvidenceArmDisposition.Inconclusive,
                    true, false, "build timed out", [check])]);
        }

        private static FocusedEvidenceRunResult IncidentRed(string request)
        {
            var checks = IncidentFailures.Select((identity, index) => new AcceptanceCheckResult(
                identity, false, 1, index == 0 ? "healthyTicks=6" : "Test execution timed out after 300000 milliseconds",
                FailingTestIdentities: [identity], ExecutedTestCount: 1)).ToArray();
            return new(request, true, false, "four failing tests", checks,
                Arms: [new(FindingEvidenceArm.Candidate, Candidate, FindingEvidenceArmDisposition.Red,
                    true, false, "candidate failed", checks)]);
        }

        public AgentRole[] RetriedRoles() => Goal.Timeline.Where(evt => evt.Kind == ProgressKind.TaskRetried)
            .Select(evt => Goal.Tasks.Single(task => task.Id == evt.TaskId).RequiredRole).ToArray();

        public string RecoveryEvidence(ConductorAdvanceResult result) =>
            result.Outcome is ConductorAdvanceOutcome.Escalated escalated
                ? Assert.IsType<PolicyDecisionRecord>(escalated.Decision).DiscriminatingEvidence!
                : Recoveries.Last().DiscriminatingEvidence;

        public ConductorAdvanceResult Advance()
        {
            var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getPreReviewEvidenceContext: _ => NoPreReviewContext(Candidate),
                getLandingFileScopes: _ => [_incident ? HoldPath : CorePath], executionDirectory: _root,
                focusedEvidenceAttemptCoordinator: Coordinator(),
                runFocusedEvidence: (_, request) => ++Runs == 1 ? Unusable(request) : IncidentRed(request),
                dispatchAndStart: _ =>
                {
                    var task = Goal.Tasks.First(candidate => candidate.Status == WorkTaskStatus.Assigned &&
                        Goal.Tasks.TakeWhile(predecessor => predecessor.Id != candidate.Id)
                            .All(predecessor => predecessor.Status == WorkTaskStatus.Completed));
                    DispatchedRoles.Add(task.RequiredRole);
                    Kernel.RecordTaskDispatch(Goal.Id, task.Id, new("fixture", "work", _root, _clock.Next()));
                    return DispatchStartOutcome.Started();
                },
                retryTaskWithCause: (gid, tid, message, round, cause) =>
                    Kernel.RetryTask(gid, tid, message, retryRoundKind: round, retryCause: cause),
                recordTaskNote: (gid, tid, message) => Kernel.RecordTaskNote(gid, tid, message),
                recordFindingEvidenceRequest: (gid, tid, message) => Kernel.RecordFindingEvidenceRequest(gid, tid, message),
                recordFindingEvidenceRun: (gid, tid, message) => Kernel.RecordFindingEvidenceRun(gid, tid, message),
                beforeFailedGoalRecoveryEffect: (_, decision) => Recoveries.Add(decision),
                emptyOutputBackoffDelay: _ => { },
                writeEscalation: (_, _, reason) => Escalations.Add(reason));
            if (_baseline) driver.InconclusiveRedRoute = (_, _, _) => AttributedFailureRouteDecision.Decline("baseline");
            driver.OverrideDeveloperCompletionStructuralPreflightForTests(
                _ => new DeveloperCompletionStructuralFindings(false, "clean"), TimeSpan.FromSeconds(5));
            return driver.AdvanceOnce(Goal, ConductorAutonomyPolicy.Permissive);
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class FixtureClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Next() => UtcNow = UtcNow.AddMinutes(1);
    }
}
