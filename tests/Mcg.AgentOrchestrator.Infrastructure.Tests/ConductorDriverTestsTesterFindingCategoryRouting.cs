using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsTesterFindingCategoryRouting
{
    [Xunit.Fact]
    public void RetainedMixedFindingsRouteCheckpointOwnerOnceAcrossReload()
    {
        var (kernel, goal) = RetainedMixedFindings();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var checkpointDispatch = developer.DispatchHistory[0];
        Xunit.Assert.Equal("a104a98deeed4719", DispatchIdentity.BuildId(goal.Id, developer.Id, checkpointDispatch));
        Xunit.Assert.Equal(checkpointDispatch.FailedRoundCheckpointDecision!.DispatchId,
            DispatchIdentity.BuildId(goal.Id, developer.Id, checkpointDispatch));
        Xunit.Assert.Null(checkpointDispatch.ResultCommit);
        Xunit.Assert.All(developer.VerificationHistory, record => Xunit.Assert.False(record.HasCommittedChanges));
        Xunit.Assert.Equal(RetainedCandidate, developer.LastDispatch!.BaseCommit);
        Xunit.Assert.Equal(RetainedCandidate, developer.LastDispatch.ResultCommit);
        Xunit.Assert.Equal(RetainedCandidate, tester.LastVerification!.ReviewedCommit);
        Xunit.Assert.True(VerifyingFindingCurrency.IsCurrent(goal, tester, tester.LastVerification));
        Xunit.Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        Xunit.Assert.Equal(WorkTaskStatus.Failed, tester.Status);
        var retries = new List<(TaskId Target, string Message, RetryCause Cause)>();
        var focusedRuns = 0;
        ConductorDriver Driver() => MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => NoPreReviewContext(RetainedCandidate),
            dispatchAndStart: _ => DispatchStartOutcome.EmptyBatch("fixture holds dispatch after routing"),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult(request, true, true, "unexpected focused execution", []);
            },
            retryTaskWithCause: (goalId, taskId, message, kind, cause) =>
            {
                retries.Add((taskId, message, cause));
                return kernel.RetryTaskAutomatically(goalId, taskId, message, cause, retryRoundKind: kind);
            });

        Driver().AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        // Negative control without checkpoint ownership recognition: HasEverCommittedOutput
        // leaves RequiresCommittedTarget true with no explicit target.
        var retry = Xunit.Assert.Single(retries);
        Xunit.Assert.Equal(developer.Id, retry.Target);
        var repair = retry.Message.Split("## RESIDUAL_OPEN_ACTION_ITEMS")[1].Split("## PRESERVE_ACCEPTED")[0];
        Xunit.Assert.Contains("compiler-cap-diagnostic-path-equivalence", repair);
        Xunit.Assert.Contains("compiler-cap-native-executor-contract-coverage", repair);
        Xunit.Assert.DoesNotContain("compiler-cap-wrapper-compatibility-evidence", repair);
        Xunit.Assert.Contains("advisory_count: 1", retry.Message);
        Xunit.Assert.Contains("compiler-cap-wrapper-compatibility-evidence",
            retry.Message.Split("## DEFERRED_NON_BLOCKING_ADVISORIES")[1]);
        AssertRetainedAdvisory(goal.Tasks.Single(task => task.Id == tester.Id));
        Driver().AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        var persisted = JsonSerializer.Serialize(kernel.ExportSnapshot());
        kernel = AgentOrchestratorKernel.FromSnapshot(JsonSerializer.Deserialize<OrchestratorSnapshot>(persisted)!);
        goal = kernel.GetGoal(goal.Id);
        Driver().AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Xunit.Assert.Single(retries);
        Xunit.Assert.Equal(0, focusedRuns);
        Xunit.Assert.Single(goal.Timeline.Where(item => item.Kind == ProgressKind.TaskRetried && item.TaskId == developer.Id));
        Xunit.Assert.DoesNotContain(goal.Timeline, item => item.Kind == ProgressKind.TaskRetried && item.TaskId == tester.Id);
        AssertRetainedAdvisory(goal.Tasks.Single(task => task.Id == tester.Id));
    }

    [Xunit.Theory]
    [Xunit.InlineData("missing")]
    [Xunit.InlineData("failed")]
    [Xunit.InlineData("wrong-dispatch")]
    [Xunit.InlineData("empty-commit")]
    [Xunit.InlineData("unchanged-commit")]
    [Xunit.InlineData("stale-candidate")]
    [Xunit.InlineData("stale-attempt")]
    [Xunit.InlineData("ambiguous")]
    public void RetainedMixedFindingsWithoutAuthoritativeCheckpointEscalate(string control)
    {
        var (kernel, goal) = RetainedMixedFindings(control);
        var retries = new List<TaskId>();
        string? escalation = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            getPreReviewEvidenceContext: _ => NoPreReviewContext(RetainedCandidate),
            retryTaskWithCause: (goalId, taskId, message, kind, cause) =>
            {
                retries.Add(taskId);
                return kernel.RetryTaskAutomatically(goalId, taskId, message, cause, retryRoundKind: kind);
            },
            writeEscalation: (_, _, message) => escalation = message);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Xunit.Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Xunit.Assert.Empty(retries);
        Xunit.Assert.Contains("could not be routed to an upstream Developer task", escalation);
    }

    private const string RetainedCandidate = "ff8df75a0855771c32b8d562c66a08e3cd444766";
    private const string RetainedCheckpoint = "d1a712d9430aad7192f9559da6c9909d3a8f6a5f";

    private static void AssertRetainedAdvisory(TaskSpec tester)
    {
        var finding = Xunit.Assert.Single(tester.VerificationHistory.Last().MergedReviewFindings!,
            item => item.StableId == "compiler-cap-wrapper-compatibility-evidence");
        Xunit.Assert.Equal(ReviewFindingState.Open, finding.State);
        Xunit.Assert.Equal(FindingSeverity.Advisory, finding.Severity);
        Xunit.Assert.Equal(FindingCategory.AcceptanceOwned, finding.Category);
        var selection = Xunit.Assert.Single(finding.EvidenceRequest!.Selections);
        Xunit.Assert.Equal("FullyQualifiedName~WorkerBuildDiagnosticDefaultsTests", selection.TestClass);
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal) RetainedMixedFindings(string control = "valid")
    {
        // Retained task-history.json and incident-timeline.json under operator-evidence/
        // tester-routing-20261028, cutoff 00:40Z before the manual Developer retry.
        // ff8 is an integration merge whose first parent is the typed committed checkpoint.
        const string goalId = "45469eead6f54022b9a2f9f3e32d695a";
        const string developerId = "2d0c7585098040d6aa868bd8568a49bb";
        const string testerId = "f944bdc5c3104af09173b1bd55576fa8";
        const string directory = @"C:\Users\miles\vcs\mcg-agent-orchestrator\.orchestrator-worktrees\45469eea";
        const string command = "codex exec --json --skip-git-repo-check --model 'gpt-6.1-sol' -c model_reasoning_effort='high' -c project_doc_max_bytes=65536 --sandbox 'danger-full-access' --cd '" + directory + "'";
        var firstDispatch = new TaskDispatchSnapshot("codex-cli", command, directory,
            DateTimeOffset.Parse("2026-10-27T22:00:24.4560594Z"),
            BaseCommit: "7b393558262c05c2e3f8d0cbdcbda6e2c7387659",
            FailedRoundCheckpointDecision: control == "missing" ? null : new FailedRoundCheckpointDecision(
                control == "wrong-dispatch" ? "another-dispatch" : "a104a98deeed4719",
                control == "failed" ? FailedRoundCheckpointOutcome.CommitFailed : FailedRoundCheckpointOutcome.Committed,
                control == "empty-commit" ? "" : control == "unchanged-commit"
                    ? "7b393558262c05c2e3f8d0cbdcbda6e2c7387659" : RetainedCheckpoint, null));
        var noChangeCandidate = control == "stale-candidate" ? new string('b', 40) : RetainedCandidate;
        var noChangeDispatch = new TaskDispatchSnapshot("codex-cli", command, directory,
            DateTimeOffset.Parse("2026-10-28T00:18:22.3877499Z"), BaseCommit: noChangeCandidate, ResultCommit: noChangeCandidate);
        var failedDeveloper = new TaskVerificationSnapshot(command, directory, 1, "failed build evidence", "",
            DateTimeOffset.Parse("2026-10-27T22:21:16.862873Z"), WorkerResultPresent: true);
        var completedDeveloper = new TaskVerificationSnapshot(command, directory, 0, "no-change build evidence", "",
            DateTimeOffset.Parse("2026-10-28T00:21:22.8188057Z"), WorkerResultPresent: true);
        var findings = RetainedFindings();
        var testerDispatch = new TaskDispatchSnapshot("codex-cli", command, directory,
            DateTimeOffset.Parse("2026-10-28T00:28:30.4513759Z"), BaseCommit: RetainedCandidate, ResultCommit: RetainedCandidate);
        var testerOutput = string.Join(Environment.NewLine,
            "WORKER_RESULT:", "files: none", "commands: inspect candidate", "tests: deferred - WorkerBuildDiagnosticDefaultsTests",
            "commit: none", "blockers: none", $"findings: {JsonSerializer.Serialize(findings)}",
            "model_fit: fixture/model - adequate - routing fixture", "skills: none", "confidence: high", "END_WORKER_RESULT");
        var testerVerification = new TaskVerificationSnapshot(command, directory, 0, testerOutput, "",
            DateTimeOffset.Parse("2026-10-28T00:31:52.4372648Z"), ReviewedCommit: RetainedCandidate,
            MergedReviewFindings: findings, WorkerResultPresent: true,
            AuthoritativeStandardOutput: testerOutput,
            DispatchStartedAt: DateTimeOffset.Parse("2026-10-28T00:28:32.6392527Z"));
        var (seed, _) = SoftwareGoal();
        var snapshot = seed.ExportSnapshot().Goals.Single();
        var tasks = snapshot.Tasks.Select(task => task.RequiredRole switch
        {
            AgentRole.Developer => task with { Id = developerId, Status = WorkTaskStatus.Completed,
                LastVerification = completedDeveloper, VerificationHistory = [failedDeveloper, completedDeveloper],
                LastDispatch = noChangeDispatch, DispatchHistory = [firstDispatch, noChangeDispatch],
                LatestRetryAt = DateTimeOffset.Parse(control == "stale-attempt"
                    ? "2026-10-28T00:20:00Z" : "2026-10-28T00:16:09.296268Z") },
            AgentRole.Tester => task with { Id = testerId, Status = WorkTaskStatus.Failed,
                LastVerification = testerVerification, VerificationHistory = [testerVerification],
                LastDispatch = testerDispatch, DispatchHistory = [testerDispatch] },
            AgentRole.Planner => task with { Status = WorkTaskStatus.Completed },
            _ => task
        }).ToList();
        if (control == "ambiguous")
        {
            var owner = tasks.Single(task => task.RequiredRole == AgentRole.Developer);
            // A second authoritative committed owner must not be selected by task order alone.
            tasks.Insert(0, owner with { Id = "other-developer", LastDispatch = noChangeDispatch with
                { BaseCommit = "other-base", ResultCommit = RetainedCandidate },
                DispatchHistory = [noChangeDispatch with { BaseCommit = "other-base", ResultCommit = RetainedCandidate }] });
        }
        var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [snapshot with { Id = goalId, Tasks = tasks, Timeline = [] }], []));
        var goal = kernel.GetGoal(new GoalId(goalId));
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        // Restored subscription reports require authoritative stdout before finding routing.
        // A preview alone is unreadable and would exercise only terminal escalation.
        Xunit.Assert.Equal(testerOutput, tester.LastVerification!.AuthoritativeStandardOutput);
        Xunit.Assert.Equal(VerificationTerminalContractStatus.Valid,
            WorkerResultBlockers.AssessSubscriptionVerification(tester, tester.LastVerification).Status);
        return (kernel, goal);
    }

    private static ReviewFinding[] RetainedFindings() =>
    [
        new("compiler-cap-diagnostic-path-equivalence", ReviewFindingState.Open,
            new("scripts/CompilerCapExperiment.psm1", "Get-CapNormalizedPath, lines 138-146"),
            "Equivalent repository-relative and absolute diagnostic paths produce different keys: src/Program.cs remains unchanged while its absolute equivalent becomes <repo>/src/Program.cs. This violates the specified normalization contract and creates false diagnostic mismatches. Canonicalize both forms and add an entrypoint case; existing normalization coverage tests owned-root equivalence only.",
            FindingSeverity.Blocking, FindingCategory.SpecCompliance),
        new("compiler-cap-native-executor-contract-coverage", ReviewFindingState.Open,
            new("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/CompilerCapExperimentContractTests.cs", "Fixture and TerminalPhaseFailureRetainsEvidenceSkipsLaterArmsAndNeverRetries"),
            "Fixtures use MUST-NOT-LAUNCH and supply phase status, CleanupConfirmed and OutputComplete directly. The green cases verify report/state handling but bypass Invoke-CapNativePhase, leaving native argument forwarding, ownership assignment, timeout termination, output draining and cleanup unverified. Add a controllable process-host boundary or bounded fake executable that exercises those actions without launching the App benchmark.",
            FindingSeverity.Blocking, FindingCategory.TestCoverage),
        new("compiler-cap-wrapper-compatibility-evidence", ReviewFindingState.Open,
            new("scripts/Invoke-WorkerBuildDiagnostic.ps1", "Optional observation seam"),
            "The shared diagnostic wrapper changed. Planner and documentation require WorkerBuildDiagnosticDefaultsTests, but the supplied candidate receipt covers only CompilerCapExperimentContractTests. Request this additional selection; do not repeat the covered 57 cases on the unchanged candidate.",
            FindingSeverity.Advisory, FindingCategory.AcceptanceOwned,
            new([new("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", "FullyQualifiedName~WorkerBuildDiagnosticDefaultsTests")]))
    ];

    [Xunit.Fact]
    public void IncompleteReceiptOnTestEvidenceFindingRunsFocusedEvidenceWithoutDeveloperRetry()
    {
        const string candidateSha = "abc1234";
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        PassVerification(kernel, goal, developer, hasCommittedChanges: true);
        DispatchTask(kernel, goal, tester, "test");
        var request = new FindingEvidenceRequest(
            [new FindingEvidenceSelection("Infrastructure.Tests", "ConductorDriverTests")]);
        var finding = new ReviewFinding("current-receipt", ReviewFindingState.Open,
            new ReviewFindingLocation("tests/Receipt.cs", "Receipt.Class"),
            "Focused test evidence still needs a finding disposition.",
            FindingSeverity.Blocking, FindingCategory.TestEvidence, request);
        var output = string.Join(Environment.NewLine,
            "WORKER_RESULT:", "files: none", "commands: inspect receipt",
            "tests: fail - current receipt needs disposition", "commit: none",
            "blockers: exact-blocker - current receipt needs disposition",
            $"findings: {JsonSerializer.Serialize(new[] { finding })}",
            "touched_anchors: []", "verdict: needs-work",
            "model_fit: fixture/model - adequate - routing fixture", "skills: none",
            "confidence: high", "END_WORKER_RESULT");
        kernel.RecordDispatchExecutionResult(goal.Id, tester.Id, new TaskVerificationRecord(
            "test", @"C:\tmp", 1, output, string.Empty, DateTimeOffset.UtcNow,
            WorkerResultPresent: true));
        kernel.RecordFindingEvidenceOutcome(goal.Id, tester.Id, finding.StableId,
            new FindingEvidenceOutcome(true, "current-receipt-id",
                ResultReason: FindingEvidenceOutcomeReason.ValidEvidence),
            new FindingEvidenceReceipt("current-receipt-id", candidateSha, request,
                Accepted: true, Passed: true, "focused class passed",
                RequestDispositions:
                [
                    new FindingEvidenceRequestDisposition(finding.StableId,
                        "Infrastructure.Tests:ConductorDriverTests", "executed-standalone", "only-request")
                ]));

        var focusedRuns = 0;
        TaskId? retriedTaskId = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            runFocusedEvidence: (_, actualRequest) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult(actualRequest, true, true, "unexpected run", []);
            },
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskId = taskId;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            });

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Xunit.Assert.Equal(1, focusedRuns);
        Xunit.Assert.NotEqual(developer.Id, retriedTaskId);
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void PermanentlyDeclinedTestEvidenceRequestRoutesByOtherFindings(bool includeUnspecifiedFinding)
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        PassVerification(kernel, goal, developer, hasCommittedChanges: true);
        DispatchTask(kernel, goal, tester, "test");
        var finding = new ReviewFinding("declined-evidence", ReviewFindingState.Open,
            new ReviewFindingLocation("tests/Receipt.cs", "Receipt.Class"),
            "Focused evidence request was declined.", FindingSeverity.Blocking,
            FindingCategory.TestEvidence,
            new FindingEvidenceRequest([new FindingEvidenceSelection("Infrastructure.Tests", "ConductorDriverTests")]));
        var findings = includeUnspecifiedFinding
            ? new[] { finding, new ReviewFinding("uncategorized-code-defect", ReviewFindingState.Open,
                new ReviewFindingLocation("src/Worker.cs", "Worker.Run"),
                "Developer must repair the source defect.", FindingSeverity.Blocking) }
            : [finding];
        var output = string.Join(Environment.NewLine,
            "WORKER_RESULT:", "files: none", "commands: inspect receipt",
            "tests: fail - focused evidence was declined", "commit: none",
            "blockers: exact-blocker - focused evidence was declined",
            $"findings: {JsonSerializer.Serialize(findings)}",
            "touched_anchors: []", "verdict: needs-work",
            "model_fit: fixture/model - adequate - routing fixture", "skills: none",
            "confidence: high", "END_WORKER_RESULT");
        kernel.RecordDispatchExecutionResult(goal.Id, tester.Id, new TaskVerificationRecord(
            "test", @"C:\tmp", 1, output, string.Empty, DateTimeOffset.UtcNow,
            WorkerResultPresent: true));
        kernel.RecordFindingEvidenceOutcome(goal.Id, tester.Id, finding.StableId,
            new FindingEvidenceOutcome(false, null,
                FindingEvidenceNotHonouredReason.UnsupportedProject,
                ResultReason: FindingEvidenceOutcomeReason.Unknown));

        var focusedRuns = 0;
        TaskId? retriedTaskId = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult(request, true, true, "unexpected run", []);
            },
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskId = taskId;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            });

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Xunit.Assert.Equal(0, focusedRuns);
        Xunit.Assert.Equal(includeUnspecifiedFinding ? developer.Id : tester.Id, retriedTaskId);
    }

    [Xunit.Fact]
    public void UntypedTesterBlockerMentioningVerificationCommandStillRetriesDeveloper()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        PassVerification(kernel, goal, developer, hasCommittedChanges: true);
        FailTesterBlocker(kernel, goal, tester,
            "Developer must repair the verification command integration.");
        TaskId? retriedTaskId = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskId = taskId;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            });

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Xunit.Assert.Equal(developer.Id, retriedTaskId);
    }
}
