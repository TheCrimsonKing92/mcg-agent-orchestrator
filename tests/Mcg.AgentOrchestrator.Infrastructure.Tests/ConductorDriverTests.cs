using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Xml.Linq;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class ConductorDriverTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static IReadOnlyList<AgentDefinition> DefaultAgents() => AgentCatalog.Default().Agents;

    private static (AgentOrchestratorKernel Kernel, Goal Goal) SimpleGoal(string objective = "Test goal")
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), objective);
        return (kernel, goal);
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal) SoftwareGoal(string objective = "Review retry goal")
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateGoal(kernel, DefaultAgents(), objective);
        return (kernel, goal);
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal) ReviewGoalWithoutTester()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Pre-review evidence goal",
            [
                new TaskSpec(TaskId.New(), "Implement the change", AgentRole.Developer),
                new TaskSpec(TaskId.New(), "Review the change", AgentRole.Reviewer)
            ]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        return (kernel, goal);
    }

    private static void DispatchTask(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        string command = "test.exe",
        string? reviewFindingTouchProofDiagnostic = null,
        IReadOnlyList<ReviewFindingLocation>? reviewFindingTouchedAnchors = null,
        string workingDirectory = "C:\\tmp")
    {
        var dispatch = new TaskDispatchRecord(
            "test-worker",
            command,
            workingDirectory,
            DateTimeOffset.UtcNow,
            ReviewFindingTouchedAnchors: reviewFindingTouchedAnchors,
            ReviewFindingTouchProofDiagnostic: reviewFindingTouchProofDiagnostic);
        kernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);
    }

    private static void PassVerification(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        bool hasCommittedChanges = false)
    {
        DispatchTask(kernel, goal, task);
        var verification = new TaskVerificationRecord(
            "test.exe",
            "C:\\tmp",
            0,
            "ok",
            "",
            DateTimeOffset.UtcNow,
            HasCommittedChanges: hasCommittedChanges);
        kernel.RecordTaskVerification(goal.Id, task.Id, verification);
    }

    private static void FailVerification(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task)
    {
        DispatchTask(kernel, goal, task);
        // RecordDispatchExecutionResult sets WorkTaskStatus.Failed; RecordTaskVerification does not
        var verification = new TaskVerificationRecord("test.exe", "C:\\tmp", 1, "fail", "error", DateTimeOffset.UtcNow);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, verification);
    }

    private static void FailReviewerNeedsWork(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec reviewer,
        string blocker,
        string? evidenceRequest = null,
        string? stdoutPath = "C:\\tmp\\reviewer.out.log",
        IReadOnlyList<ReviewFinding>? findings = null)
    {
        DispatchTask(kernel, goal, reviewer, "review");
        var effectiveFindings = (findings ?? blocker
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select((finding, index) => new ReviewFinding(
                    $"finding-{index + 1}",
                    ReviewFindingState.Open,
                    new ReviewFindingLocation("src/Test.cs", $"Test.Run{index + 1}"),
                    finding))
                .ToArray())
            .ToArray();
        if (!string.IsNullOrWhiteSpace(evidenceRequest))
        {
            var parts = evidenceRequest.Split(':', 2, StringSplitOptions.TrimEntries);
            var selections = parts[1]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(testClass => new FindingEvidenceSelection(
                    parts[0],
                    testClass))
                .ToArray();
            effectiveFindings[0] = effectiveFindings[0] with
            {
                EvidenceRequest = new FindingEvidenceRequest(selections)
            };
        }
        var lines = new List<string>
        {
            "Findings first.",
            "WORKER_RESULT:",
            "files: none",
            "commands: review",
            "tests: pass - inspected evidence",
            $"blockers: {blocker}",
            $"findings: {JsonSerializer.Serialize(effectiveFindings)}",
            "touched_anchors: []"
        };
        lines.Add("verdict: needs-work");
        lines.Add("END_WORKER_RESULT");
        var stdout = string.Join(Environment.NewLine, lines);
        var verification = new TaskVerificationRecord(
            "review",
            "C:\\tmp",
            1,
            stdout,
            "",
            DateTimeOffset.UtcNow,
            StandardOutputPath: stdoutPath,
            WorkerResultPresent: true);
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, verification);
    }

    private static void FailStructuredReviewerRound(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec reviewer,
        string command,
        IReadOnlyList<ReviewFinding> findings)
    {
        var stdout = string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "commands: review",
            "tests: pass - inspected evidence",
            $"blockers: {string.Join("; ", findings.Select(finding => finding.Description))}",
            $"findings: {JsonSerializer.Serialize(findings)}",
            "touched_anchors: []",
            "verdict: needs-work",
            "END_WORKER_RESULT");
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            command,
            "C:\\tmp",
            1,
            stdout,
            string.Empty,
            DateTimeOffset.UtcNow,
            StandardOutputPath: "C:\\tmp\\reviewer.out.log",
            WorkerResultPresent: true));
    }

    private static void SeedReviewerIdentityViolation(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec reviewer,
        string? touchProofDiagnostic = null)
    {
        DispatchTask(kernel, goal, reviewer, "review-seed");
        var location = new ReviewFindingLocation("src/A.cs", "A.Run", "guard");
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-seed",
            "C:\\tmp",
            0,
            ReviewerPassWithAdvisory("A-1", location),
            "",
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true));
        kernel.RetryTask(goal.Id, reviewer.Id, "fresh review");
        RecordMovedReviewerIdentityViolation(
            kernel,
            goal,
            reviewer,
            touchProofDiagnostic);
    }

    private static void RecordMovedReviewerIdentityViolation(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec reviewer,
        string? touchProofDiagnostic = null)
    {
        DispatchTask(kernel, goal, reviewer, "review-moved", touchProofDiagnostic);
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-moved",
            "C:\\tmp",
            0,
            ReviewerPassWithAdvisory(
                "A-1",
                new ReviewFindingLocation("src/B.cs", "B.Run", "guard")),
            "",
            DateTimeOffset.UtcNow,
            StandardOutputPath: "C:\\tmp\\reviewer.out.log",
            WorkerResultPresent: true));
        Assert.NotNull(reviewer.LastVerification!.ReviewFindingContractViolation);
    }

    private static string ReviewerPassWithAdvisory(string stableId, ReviewFindingLocation location) =>
        ReviewerPassWithFinding(new ReviewFinding(
            stableId,
            ReviewFindingState.Open,
            location,
            "Readability suggestion.",
            FindingSeverity.Advisory));

    private static string ReviewerPassWithFinding(ReviewFinding finding) =>
        string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "commands: review",
            "tests: pass - inspected evidence",
            "blockers: none",
            $"findings: {JsonSerializer.Serialize(new[] { finding })}",
            "touched_anchors: []",
            "verdict: pass",
            "END_WORKER_RESULT");

    private static void FailTesterBlocker(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec tester,
        string blocker,
        string? stdoutPath = "C:\\tmp\\tester.out.log")
    {
        DispatchTask(kernel, goal, tester, "test");
        var stdout = string.Join(
            Environment.NewLine,
            "Tests found a correctness issue.",
            "WORKER_RESULT:",
            "files: none",
            "commands: test",
            "tests: fail - focused behavior check failed",
            $"blockers: {blocker}",
            "commit: none",
            "model_fit: test",
            "skills: none",
            "confidence: high",
            "END_WORKER_RESULT");
        var verification = new TaskVerificationRecord(
            "test",
            "C:\\tmp",
            1,
            stdout,
            "",
            DateTimeOffset.UtcNow,
            StandardOutputPath: stdoutPath,
            WorkerResultPresent: true);
        kernel.RecordDispatchExecutionResult(goal.Id, tester.Id, verification);
    }

    private static void RecordInconclusiveTester(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec tester,
        string receipt)
    {
        DispatchTask(kernel, goal, tester, "test-inconclusive");
        var stdout = string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "commands: dotnet test --no-build --filter Focused",
            $"tests: inconclusive - {receipt}",
            "commit: none",
            "blockers: none",
            "model_fit: fixture/model - adequate - tester retry",
            "skills: dotnet-windows-build-hygiene",
            "confidence: medium",
            "END_WORKER_RESULT");
        kernel.RecordDispatchExecutionResult(
            goal.Id,
            tester.Id,
            new TaskVerificationRecord(
                "test-inconclusive",
                "C:\\tmp",
                0,
                stdout,
                "",
                DateTimeOffset.UtcNow,
                WorkerResultPresent: true));
    }

    private static WorkerSandboxPrepRecoverableAction NewSandboxRecoveryAction() =>
        new(
            Worktree: @"C:\repo\.orchestrator-worktrees\abc12345",
            SandboxRoot: @"C:\repo\.orchestrator-worktrees\abc12345\.mcg-sandbox",
            FailedRoot: @"C:\repo\.orchestrator-worktrees\abc12345",
            Reason: "Failed to apply inheritable Low integrity label.",
            RequiresRecursiveRemediation: true);

    private static GoalWorktreeRebaseResult DefaultRebaseSuccess() =>
        new(GoalWorktreeRebaseStatus.AlreadyFastForwardable, "goal/test", "Already fast-forwardable", [], null);

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mcg-conductor-driver-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void RunGit(string workingDirectory, params string[] args)
    {
        var result = GitCli.Run(workingDirectory, args);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Error}");
        }
    }

    private static string ReadGit(string workingDirectory, params string[] args)
    {
        var result = GitCli.Run(workingDirectory, args);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Error}");
        }

        return result.Output.Trim();
    }

    private sealed record SeededGitRepository(string WorkingDirectory, string Head) : IDisposable
    {
        public void Dispose()
        {
            try { Directory.Delete(WorkingDirectory, recursive: true); } catch { }
        }
    }

    private static SeededGitRepository CreateSeededGitRepository()
    {
        var workingDirectory = CreateTempDirectory();
        RunGit(workingDirectory, "init");
        RunGit(workingDirectory, "config", "user.email", "test@example.com");
        RunGit(workingDirectory, "config", "user.name", "Test User");
        File.WriteAllText(Path.Combine(workingDirectory, "seed.txt"), "seed");
        RunGit(workingDirectory, "add", "seed.txt");
        RunGit(workingDirectory, "commit", "-m", "seed");
        var head = ReadGit(workingDirectory, "rev-parse", "HEAD");
        return new SeededGitRepository(workingDirectory, head);
    }

    private static int CountOccurrences(string value, string expected)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(expected, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += expected.Length;
        }

        return count;
    }

    private static GoalLifecycleFacts ReadFactsPerGoal(OrchestratorWorkspace workspace, Goal goal)
    {
        var dir = workspace.ExecutionDirectory;
        var workspaceExists = GoalWorktrees.TryResolve(dir, goal.Id) is not null;
        var journal = GoalOperationJournal.Read(dir, goal.Id);
        var isMerged = GoalOperationJournal.HasCompletedLandingEvidence(journal);
        var isRecorded = GoalOperationJournal.HasCompletedRecordEvidence(journal);
        var isCleanedUp = GoalOperationJournal.HasCompletedCleanupEvidence(journal);
        var hasOpenClarification = GoalRefinementGate.HasOpenClarification(workspace, goal);
        return new GoalLifecycleFacts(workspaceExists, IsBlocked: false, isMerged, isRecorded, isCleanedUp, hasOpenClarification);
    }

    private static ConductorDriver MakeDriver(
        Func<Goal, GoalLifecycleFacts>? getFacts = null,
        Func<int>? getRunningCount = null,
        Func<Goal, string>? createWorkspace = null,
        Func<Goal, DispatchStartOutcome>? dispatchAndStart = null,
        Func<Goal, DispatchStartOutcome>? startRecordedDispatches = null,
        Action? buildServerShutdown = null,
        Func<Goal, bool>? runAcceptance = null,
        Func<Goal, AcceptanceVerificationSummary>? runAcceptanceSummary = null,
        Action<Goal, AcceptanceVerificationSummary>? runAdvisorySemanticAcceptance = null,
        Func<Goal, string, FocusedEvidenceRunResult>? runFocusedEvidence = null,
        Func<GoalId, TaskId, string, TaskSpec>? retryTask = null,
        Func<GoalId, TaskId, string, RetryRoundKind?, TaskSpec>? retryTaskWithRoundKind = null,
        Action<GoalId, TaskId, string>? recordTaskNote = null,
        Action<GoalId, TaskId, string>? recordReviewerEvidenceRequestReceived = null,
        Action<GoalId, TaskId, string>? recordReviewerEvidenceRunRecorded = null,
        Func<GoalId, TaskId, IReadOnlyList<string>, int>? recordCriterionRetryFeedback = null,
        Action<GoalId, TaskId>? clearCriterionRetryFeedback = null,
        Func<Goal, GoalWorktreeRebaseResult>? rebaseOntoMain = null,
        Func<Goal, LandingResult>? land = null,
        Action<Goal, LandingResult>? afterSuccessfulLanding = null,
        Action<Goal>? record = null,
        Func<Goal, GoalWorktreeRemoveResult>? cleanup = null,
        Action<Goal, GoalLifecycleState, string>? writeEscalation = null,
        Func<Goal, ChangeRiskTier?>? classifyRisk = null,
        Action<TimeSpan>? emptyOutputBackoffDelay = null,
        Func<Goal, DispatchReadinessVerdict>? evaluateReadiness = null,
        Action<Goal>? completeGoal = null,
        Func<Goal, string, bool>? normalizeLifecycleState = null,
        Func<WorkerSandboxPrepRecoverableAction, bool>? recoverSandboxPrep = null,
        Func<bool>? hasGateReadyGoal = null,
        Func<Goal, int>? getAcceptanceSlotCount = null,
        Func<int>? getWorkerAdmissionCapacity = null,
        TimeSpan? buildServerShutdownTimeout = null,
        Func<Goal, GoalLifecycleState, string, LandingEscalationWriteResult>? writeEscalationWithResult = null,
        Func<Goal, PreReviewEvidenceContext>? getPreReviewEvidenceContext = null,
        Action<GoalId, TaskId, PreReviewEvidenceReceipt>? recordPreReviewEvidence = null,
        Action<GoalId, TaskId, string, int>? recordPreReviewMappingEscalationSuppressed = null,
        ConductorParallelAcceptanceAttemptCoordinator? focusedEvidenceAttemptCoordinator = null,
        Action<GoalId, TaskId, string, FindingEvidenceOutcome, FindingEvidenceReceipt?>? recordFindingEvidenceOutcome = null,
        Action<GoalId, TaskId, string>? recordFindingEvidenceRequest = null,
        Action<GoalId, TaskId, string>? recordFindingEvidenceRun = null,
        Func<Goal, AcceptanceGateEngineSettings>? getFindingEvidenceEngineSettings = null,
        Func<Goal, bool>? isVerificationGateSatisfied = null,
        GateReadyCandidateProjector? gateReadyCandidateProjector = null)
    {
        return new ConductorDriver(
            getFacts ?? (_ => GoalLifecycleFacts.None),
            getRunningCount ?? (() => 0),
            createWorkspace ?? (_ => "/tmp/workspace"),
            dispatchAndStart ?? (_ => DispatchStartOutcome.Started()),
            startRecordedDispatches,
            buildServerShutdown,
            runAcceptanceSummary ?? (goal => (runAcceptance ?? (_ => true))(goal)
                ? AcceptanceVerificationSummary.PassedWithNoUnmetCriteria
                : AcceptanceVerificationSummary.Failed),
            runAdvisorySemanticAcceptance,
            retryTask,
            recordTaskNote,
            recordCriterionRetryFeedback,
            clearCriterionRetryFeedback,
            rebaseOntoMain ?? (_ => DefaultRebaseSuccess()),
            land is null
                ? ((g, _) => new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed"))
                : ((g, _) => land(g)),
            afterSuccessfulLanding,
            record ?? (_ => { }),
            cleanup ?? (_ => new GoalWorktreeRemoveResult("Workspace cleaned up.", null, [], null)),
            writeEscalation ?? ((_, _, _) => { }),
            classifyRisk ?? (_ => null),
            emptyOutputBackoffDelay,
            evaluateReadiness,
            completeGoal: completeGoal,
            normalizeLifecycleState: normalizeLifecycleState,
            recoverSandboxPrep: recoverSandboxPrep,
            hasGateReadyGoal: hasGateReadyGoal,
            getAcceptanceSlotCount: getAcceptanceSlotCount,
            getWorkerAdmissionCapacity: getWorkerAdmissionCapacity,
            runFocusedEvidence: runFocusedEvidence,
            recordReviewerEvidenceRequestReceived: recordReviewerEvidenceRequestReceived,
            recordReviewerEvidenceRunRecorded: recordReviewerEvidenceRunRecorded,
            retryTaskWithRoundKind: retryTaskWithRoundKind,
            buildServerShutdownTimeout: buildServerShutdownTimeout,
            writeEscalationWithResult: writeEscalationWithResult,
            getPreReviewEvidenceContext: getPreReviewEvidenceContext,
            recordPreReviewEvidence: recordPreReviewEvidence,
            recordPreReviewMappingEscalationSuppressed: recordPreReviewMappingEscalationSuppressed,
            focusedEvidenceAttemptCoordinator: focusedEvidenceAttemptCoordinator,
            recordFindingEvidenceOutcome: recordFindingEvidenceOutcome,
            recordFindingEvidenceRequest: recordFindingEvidenceRequest,
            recordFindingEvidenceRun: recordFindingEvidenceRun,
            getFindingEvidenceEngineSettings: getFindingEvidenceEngineSettings,
            isVerificationGateSatisfied: isVerificationGateSatisfied,
            gateReadyCandidateProjector: gateReadyCandidateProjector);
    }

    private static PreReviewEvidenceContext FocusedPreReviewContext(string sha) =>
        new(
            sha,
            [
                "dotnet test tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --verbosity minimal --filter FullyQualifiedName~ConductorDriverTests"
            ],
            "Infrastructure.Tests: FullyQualifiedName~ConductorDriverTests",
            "Orchestration change mapped to ConductorDriverTests.",
            NoApplicableTests: false,
            MappingNeedsInput: false);

    private static PreReviewEvidenceContext NoPreReviewContext(string sha) =>
        new(
            sha,
            [],
            null,
            "No mapped pre-review tests in this routing fixture.",
            NoApplicableTests: true,
            MappingNeedsInput: false);

    private static ReviewFinding EvidenceFinding(string description, string id = "missing-receipts") =>
        new(
            id,
            ReviewFindingState.Open,
            new ReviewFindingLocation("tests/receipts", id),
            description,
            FindingSeverity.Blocking,
            FindingCategory.TestEvidence);

    private static ReviewFinding EvidenceFindingWithRequest(
        string description,
        string id = "missing-receipts",
        FindingCategory category = FindingCategory.TestEvidence,
        string project = "Infrastructure.Tests",
        params string[] classes) =>
        EvidenceFinding(description, id) with
        {
            Category = category,
            EvidenceRequest = new FindingEvidenceRequest(
                (classes.Length == 0 ? ["ConductorDriverTests"] : classes)
                    .Select(testClass => new FindingEvidenceSelection(project, testClass))
                    .ToArray())
        };

    private static FocusedEvidenceRunResult PassingPreReviewEvidence(string request) =>
        new(
            request,
            Accepted: true,
            Passed: true,
            Summary: "focused evidence passed: 1 check",
            Checks:
            [
                new AcceptanceCheckResult(
                    "pre-review focused evidence",
                    true,
                    0,
                    "Passed: 7",
                    ArtifactsPath: "C:\\receipts\\green",
                    TestResultPaths: ["C:\\receipts\\green\\result.trx"])
            ]);

    private static FocusedEvidenceRunResult DualArmFindingEvidence(
        string request,
        FindingEvidenceArmDisposition baselineDisposition)
    {
        var candidateCheck = new AcceptanceCheckResult(
            "candidate focused evidence",
            true,
            0,
            null,
            ArtifactsPath: "C:\\receipts\\candidate",
            TestResultPaths: ["C:\\receipts\\candidate\\result.trx"]);
        var baselineCheck = baselineDisposition switch
        {
            FindingEvidenceArmDisposition.Green => new AcceptanceCheckResult(
                "baseline focused evidence", true, 0, null,
                ArtifactsPath: "C:\\receipts\\baseline",
                TestResultPaths: ["C:\\receipts\\baseline\\result.trx"]),
            FindingEvidenceArmDisposition.Red => new AcceptanceCheckResult(
                "baseline focused evidence", false, 1, "expected assertion failure",
                ArtifactsPath: "C:\\receipts\\baseline",
                TestResultPaths: ["C:\\receipts\\baseline\\result.trx"],
                FailingTestIdentities: ["EvidenceTests.RejectsBaseline"]),
            _ => new AcceptanceCheckResult(
                "baseline focused evidence", false, 1, "Build FAILED: error CS1002",
                ArtifactsPath: "C:\\receipts\\baseline")
        };
        var outcomeReason = baselineDisposition switch
        {
            FindingEvidenceArmDisposition.Green => FindingEvidenceOutcomeReason.VacuousEvidence,
            FindingEvidenceArmDisposition.Red => FindingEvidenceOutcomeReason.ValidEvidence,
            _ => FindingEvidenceOutcomeReason.BaselineInconclusive
        };
        return new FocusedEvidenceRunResult(
            request,
            Accepted: true,
            Passed: true,
            Summary: $"dual-arm evidence: {FindingEvidenceOutcomeReasonJsonConverter.ToWireValue(outcomeReason)}",
            Checks: [candidateCheck],
            Arms:
            [
                new FocusedEvidenceArmRunResult(
                    FindingEvidenceArm.Candidate,
                    "candidate-sha",
                    FindingEvidenceArmDisposition.Green,
                    Accepted: true,
                    Passed: true,
                    "candidate passed",
                    [candidateCheck]),
                new FocusedEvidenceArmRunResult(
                    FindingEvidenceArm.Baseline,
                    "baseline-sha",
                    baselineDisposition,
                    Accepted: true,
                    Passed: baselineDisposition == FindingEvidenceArmDisposition.Green,
                    baselineDisposition == FindingEvidenceArmDisposition.Inconclusive
                        ? "baseline did not compile"
                        : "baseline executed",
                    [baselineCheck])
            ],
            OutcomeReason: outcomeReason);
    }

    private static FocusedEvidenceRunResult CandidateRedFindingEvidence(
        string request,
        string candidateSha,
        string failingTestIdentity = "GateReadyCandidateProjectorTests.DefaultHarnessProducesSerializedResourceKey")
    {
        var candidateCheck = new AcceptanceCheckResult(
            "candidate focused evidence",
            false,
            1,
            "GateReadyCandidateProjectorTests.Harness returned ResourcesEmpty",
            ArtifactsPath: "C:\\receipts\\candidate",
            TestResultPaths: ["C:\\receipts\\candidate\\result.trx"],
            FailingTestIdentities: [failingTestIdentity]);
        var baselineCheck = new AcceptanceCheckResult(
            "baseline focused evidence",
            true,
            0,
            "Passed: 28",
            ArtifactsPath: "C:\\receipts\\baseline",
            TestResultPaths: ["C:\\receipts\\baseline\\result.trx"]);
        return new FocusedEvidenceRunResult(
            request,
            Accepted: true,
            Passed: false,
            Summary: "dual-arm evidence: candidate-red",
            Checks: [candidateCheck],
            Arms:
            [
                new FocusedEvidenceArmRunResult(
                    FindingEvidenceArm.Candidate,
                    candidateSha,
                    FindingEvidenceArmDisposition.Red,
                    Accepted: true,
                    Passed: false,
                    "candidate executed with an actionable test RED",
                    [candidateCheck]),
                new FocusedEvidenceArmRunResult(
                    FindingEvidenceArm.Baseline,
                    "baseline-sha",
                    FindingEvidenceArmDisposition.Green,
                    Accepted: true,
                    Passed: true,
                    "baseline passed",
                    [baselineCheck])
            ],
            OutcomeReason: FindingEvidenceOutcomeReason.CandidateRed);
    }

    private static PreReviewEvidenceReceipt GreenPreReviewReceipt(Goal goal, string sha) =>
        new(
            goal.Id.Value,
            1,
            sha,
            FocusedPreReviewContext(sha).SelectedFocusedTests,
            PreReviewEvidenceDisposition.Green,
            PassedCheckCount: 1,
            FailedCheckCount: 0,
            Checks: [],
            FailingTestIdentities: [],
            MappingReason: "seed",
            EvidencePointer: "C:\\receipts\\seed",
            RecordedAt: DateTimeOffset.UtcNow);

    private sealed class FakeAcceptanceVerifier : IGoalAcceptanceVerifier
    {
        public Task<AcceptanceVerificationResult> RunAsync(
            string worktreePath,
            GoalId? goalId = null,
            IReadOnlyList<string>? changedFiles = null,
            int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AcceptanceVerificationResult(true, false, 0, "ok"));

        public Task<FocusedEvidenceRunResult> RunFocusedEvidenceAsync(
            string worktreePath,
            GoalId? goalId,
            string request,
            int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null,
            bool runBaselineArm = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new FocusedEvidenceRunResult(
                request,
                Accepted: true,
                Passed: true,
                Summary: "focused evidence passed",
                Checks: [
                    new AcceptanceCheckResult(
                        "reviewer focused evidence",
                        true,
                        0,
                        null,
                        ArtifactsPath: "C:\\tmp\\focused-evidence.trx")
                ]));
    }

    private sealed class ThrowingAcceptanceVerifier(Exception exception) : IGoalAcceptanceVerifier
    {
        public Task<AcceptanceVerificationResult> RunAsync(
            string worktreePath,
            GoalId? goalId = null,
            IReadOnlyList<string>? changedFiles = null,
            int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<AcceptanceVerificationResult>(exception);

        public Task<FocusedEvidenceRunResult> RunFocusedEvidenceAsync(
            string worktreePath,
            GoalId? goalId,
            string request,
            int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null,
            bool runBaselineArm = false,
            CancellationToken cancellationToken = default) =>
            Task.FromException<FocusedEvidenceRunResult>(exception);
    }

    private sealed class CountingModelProvider(string providerName, string text) : IModelProvider
    {
        public string ProviderName { get; } = providerName;
        public int Calls { get; private set; }

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new ModelResponse(text, null, "stop"));
        }
    }

    // ── Empty-batch escalation diagnostics ───────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_named_failure_normalization_preserves_baseline_attribution")]
    public void ConductorDriverNamedFailureNormalizationPreservesBaselineAttribution()
    {
        var testResultPaths = new[] { "C:\\tmp\\focused.trx" };
        var attributions = new[]
        {
            new AcceptanceCheckAttribution(
                "core tests",
                AcceptanceFailureOrigin.Inherited,
                "also failed on main")
        };
        var acceptance = new AcceptanceVerificationSummary(
            false,
            [],
            "core tests failed",
            ["core tests"],
            "branch-a",
            "main-a",
            testResultPaths,
            attributions,
            "attested-red");

        var normalized = ConductorDriver.NormalizeNamedFailedChecksForRetry(acceptance);

        Assert.Equal(testResultPaths, normalized.TestResultPaths);
        Assert.Equal(attributions, normalized.CheckAttributions);
        Assert.Equal("attested-red", normalized.BaselineAttestation);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_real_facts_match_per_goal_read_path")]
    public void ConductorDriverRealFactsMatchPerGoalReadPath()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var merged = kernel.CreateGoal("Merged goal");
        var cleaned = kernel.CreateGoal("Cleaned goal");
        var missing = kernel.CreateGoal("Missing journal goal");
        var clarified = kernel.CreateGoal("Clarified goal");

        var worktreePath = GoalWorktrees.WorktreePath(root, merged.Id);
        Directory.CreateDirectory(worktreePath);
        File.WriteAllText(Path.Combine(worktreePath, ".git"), "gitdir: test");
        GoalOperationJournal.Completed(root, merged, "conductor:land", "landed");
        GoalOperationJournal.Completed(root, merged, "conductor:record", "recorded");
        GoalOperationJournal.Completed(root, cleaned, "conductor:cleanup", "cleaned");
        CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory)
            .RaiseAsync(
                CollaborationItemType.Clarification,
                clarified.Id.Value,
                "clarify",
                "body",
                $"spec-clarification:{clarified.Id.Value}:test")
            .GetAwaiter()
            .GetResult();

        var driver = new ConductorDriver(
            kernel,
            workspace,
            new FakeAcceptanceVerifier(),
            DefaultAgents(),
            WorkerProfileCatalog.Default());

        foreach (var goal in new[] { merged, cleaned, missing, clarified })
        {
            Assert.Equal(ReadFactsPerGoal(workspace, goal), driver.GetFacts(goal));
        }
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_real_dispatch_checkpoint_rolls_back_then_notifies_on_success")]
    public void ConductorDriverRealDispatchCheckpointRollsBackThenNotifiesOnSuccess()
    {
        var root = CreateTempDirectory();
        SeedLocalSkillCatalog(root);
        RunGit(root, "init");
        RunGit(root, "checkout", "-b", "main");
        RunGit(root, "config", "user.email", "test@example.com");
        RunGit(root, "config", "user.name", "Test User");
        File.WriteAllText(Path.Combine(root, "README.md"), "initial");
        RunGit(root, "add", ".");
        RunGit(root, "commit", "-m", "initial");

        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var (kernel, goal) = SimpleGoal("Real dispatch checkpoint rollback");
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Dispatch checkpoint rollback fixture is already refined.",
            ["The ready task reaches the real dispatch checkpoint."],
            VerificationClass.TestVerifiable,
            [],
            []));
        GoalWorktrees.Ensure(root, goal.Id);
        var before = JsonSerializer.Serialize(kernel.ExportGoalSnapshot(goal.Id));
        var persistAttempts = 0;
        var successfulCheckpointNotifications = 0;
        var profiles = WorkerProfileCatalog.Default();
        var driver = new ConductorDriver(
            kernel,
            workspace,
            new FakeAcceptanceVerifier(),
            DefaultAgents(),
            profiles,
            persistCriticalDispatchStart: (_, _) =>
            {
                persistAttempts++;
                if (persistAttempts == 1)
                    throw new SqliteException("injected busy checkpoint", 5);
            });
        driver.DispatchRecordWriteSucceededSink = _ => successfulCheckpointNotifications++;

        var ex = Assert.Throws<DispatchRecordWriteException>(() =>
            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative));

        Assert.False(ex.ProcessMayHaveStarted);
        Assert.Equal(before, JsonSerializer.Serialize(kernel.ExportGoalSnapshot(goal.Id)));
        Assert.Null(kernel.GetGoal(goal.Id).Tasks.Single().LastProcess);
        Assert.Equal(0, successfulCheckpointNotifications);

        var result = driver.AdvanceOnce(kernel.GetGoal(goal.Id), ConductorAutonomyPolicy.Conservative);

        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.Equal(4, persistAttempts);
        Assert.Equal(3, successfulCheckpointNotifications);
        Assert.NotNull(kernel.GetGoal(goal.Id).Tasks.Single().LastProcess);
    }

    [Xunit.Fact]
    public void ConductorDriverReplacementLeaseBlocksWorkspaceEvidenceMutation()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var (kernel, goal) = SimpleGoal("Lease-protected workspace creation");
        using var replacementLease = new ReconcileSweepRemediationStore(workspace.SqliteStatePath)
            .TryAcquireAcceptanceLease(
                goal.Id.Value,
                $"goal-replace:test:{Guid.NewGuid():N}",
                TimeSpan.FromMinutes(30));
        Assert.NotNull(replacementLease);
        var driver = new ConductorDriver(
            kernel,
            workspace,
            new FakeAcceptanceVerifier(),
            DefaultAgents(),
            WorkerProfileCatalog.Default());

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Equal(GoalLifecycleState.Created, held.State);
        Assert.Contains("reason=concurrent-acceptance-or-replacement", held.Reason, StringComparison.Ordinal);
        Assert.False(Directory.Exists(GoalWorktrees.WorktreePath(root, goal.Id)));
        Assert.Empty(GoalOperationJournal.Read(root, goal.Id).Entries);
    }

    [Xunit.Fact]
    public void ConductorDriverReplacementLeaseBlocksDispatchEvidenceMutation()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var (kernel, goal) = SimpleGoal("Lease-protected dispatch");
        var worktreePath = GoalWorktrees.WorktreePath(root, goal.Id);
        Directory.CreateDirectory(worktreePath);
        File.WriteAllText(Path.Combine(worktreePath, ".git"), "gitdir: test");
        using var replacementLease = new ReconcileSweepRemediationStore(workspace.SqliteStatePath)
            .TryAcquireAcceptanceLease(
                goal.Id.Value,
                $"goal-replace:test:{Guid.NewGuid():N}",
                TimeSpan.FromMinutes(30));
        Assert.NotNull(replacementLease);
        var driver = new ConductorDriver(
            kernel,
            workspace,
            new FakeAcceptanceVerifier(),
            DefaultAgents(),
            WorkerProfileCatalog.Default());
        var before = JsonSerializer.Serialize(kernel.ExportGoalSnapshot(goal.Id));
        var phaseTimings = new List<string>();
        driver.PhaseTimingSink = phaseTimings.Add;

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Contains("blocked by concurrent acceptance or replacement", held.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(phaseTimings, timing => timing.Contains("phase=dispatch-remediation", StringComparison.Ordinal));
        Assert.Equal(before, JsonSerializer.Serialize(kernel.ExportGoalSnapshot(goal.Id)));
        Assert.All(goal.Tasks, task => Assert.Null(task.LastDispatch));
        Assert.DoesNotContain(
            GoalOperationJournal.Read(root, goal.Id).Entries,
            entry => entry.Operation.StartsWith("conductor:dispatch", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void ConductorDriverReplacementLeaseBlocksAcceptanceAndLandingEvidenceMutation()
    {
        var root = CreateTempDirectory();
        RunGit(root, "init");
        RunGit(root, "checkout", "-b", "main");
        RunGit(root, "config", "user.email", "test@example.com");
        RunGit(root, "config", "user.name", "Test User");
        File.WriteAllText(Path.Combine(root, "README.md"), "initial");
        RunGit(root, "add", ".");
        RunGit(root, "commit", "-m", "initial");

        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel,
            DefaultAgents(),
            "Lease-protected conductor acceptance and landing");
        PassVerification(kernel, goal, goal.Tasks.Single());
        var worktree = GoalWorktrees.Ensure(root, goal.Id);
        File.WriteAllText(Path.Combine(worktree, "feature.txt"), "goal work");
        RunGit(worktree, "add", "feature.txt");
        RunGit(worktree, "commit", "-m", "goal work");
        var leaseOwner = $"goal-replace:test:{Guid.NewGuid():N}";
        using var replacementLease = new ReconcileSweepRemediationStore(workspace.SqliteStatePath)
            .TryAcquireAcceptanceLease(
                goal.Id.Value,
                leaseOwner,
                TimeSpan.FromMinutes(30));
        Assert.NotNull(replacementLease);
        var driver = new ConductorDriver(
            kernel,
            workspace,
            new FakeAcceptanceVerifier(),
            DefaultAgents(),
            WorkerProfileCatalog.Default());

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Contains("acceptance lease", held.Reason, StringComparison.Ordinal);
        Assert.Contains($"owner={leaseOwner}", held.Reason, StringComparison.Ordinal);
        Assert.Contains("expiresAtUtc=", held.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(
            GoalOperationJournal.Read(root, goal.Id).Entries,
            entry => entry.Operation.StartsWith("conductor:acceptance", StringComparison.Ordinal) ||
                entry.Operation.StartsWith("conductor:land", StringComparison.Ordinal));
        Assert.NotEqual(
            0,
            GitCli.Run(root, "merge-base", "--is-ancestor", GoalWorktrees.BranchName(goal.Id), "main").ExitCode);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_real_facts_refresh_after_conductor_record")]
    public async Task ConductorDriverRealFactsRefreshAfterConductorRecord()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Record refresh goal");
        PassVerification(kernel, goal, goal.Tasks.Single());
        var worktreePath = GoalWorktrees.WorktreePath(root, goal.Id);
        Directory.CreateDirectory(worktreePath);
        File.WriteAllText(Path.Combine(worktreePath, ".git"), "gitdir: test");
        GoalOperationJournal.Completed(root, goal, "conductor:land", "landed");

        var driver = new ConductorDriver(
            kernel,
            workspace,
            new FakeAcceptanceVerifier(),
            DefaultAgents(),
            WorkerProfileCatalog.Default());

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        var executed = Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.Equal(GoalLifecycleState.Merged, executed.FromState);
        Assert.True(driver.GetFacts(goal).IsRecorded);
        Assert.Equal(ReadFactsPerGoal(workspace, goal), driver.GetFacts(goal));
        var record = await new DogfoodLogStore(workspace.DogfoodLogStorePath)
            .GetByGoalIdAsync(goal.Id.Value);
        Assert.NotNull(record);
        Assert.Contains("Record refresh goal", record!.RenderedMarkdown);
        Assert.False(File.Exists(Path.Combine(root, "DOGFOOD_LOG.md")));
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_AwaitingClarification_surfaces_stale_recovery_action")]
    public async Task ConductorDriverAwaitingClarificationSurfacesStaleRecoveryAction()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var (kernel, goal) = SimpleGoal("Conductor stale clarification goal.");
        var key = $"{GoalRefinementService.CorrelationKeyPrefix}{goal.Id.Value}:api-version";
        await store.RaiseAsync(
            CollaborationItemType.Clarification,
            goal.Id.Value,
            "Spec clarification needed: Which API version?",
            "Question: Which API version?\nFork kind: external-contract",
            key);
        Assert.True(await store.TryResolveAsync(key, "REST v2"));
        await store.RaiseAsync(
            CollaborationItemType.Clarification,
            goal.Id.Value,
            "Spec clarification needed: Which API version?",
            "Question: Which API version?\nFork kind: external-contract",
            key);
        var driver = new ConductorDriver(
            kernel,
            workspace,
            new FakeAcceptanceVerifier(),
            DefaultAgents(),
            WorkerProfileCatalog.Default());

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Assert.Equal(GoalLifecycleState.AwaitingClarification, escalated.State);
        Assert.Contains("Stale spec clarification detected", escalated.Reason, StringComparison.Ordinal);
        Assert.Contains("api-version", escalated.Reason, StringComparison.Ordinal);
        Assert.Contains($"attention dismiss {goal.Id.Value[..8]}", escalated.Reason, StringComparison.Ordinal);
        var eventsPath = Path.Combine(workspace.GoalLifecycleEventsDirectory, $"{goal.Id.Value}.jsonl");
        var events = File.ReadAllText(eventsPath);
        Assert.Contains("\"eventType\":\"StaleClarificationDetected\"", events, StringComparison.Ordinal);
        Assert.Contains("\"recoveryCommand\":\"attention dismiss ", events, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_acceptance_slots_busy_journals_blocked_outcome")]
    public void ConductorDriverAcceptanceSlotsBusyJournalsBlockedOutcome()
    {
        var root = CreateTempDirectory();
        RunGit(root, "init");
        RunGit(root, "checkout", "-b", "main");
        RunGit(root, "config", "user.email", "test@example.com");
        RunGit(root, "config", "user.name", "Test User");
        File.WriteAllText(Path.Combine(root, "README.md"), "initial");
        RunGit(root, "add", ".");
        RunGit(root, "commit", "-m", "initial");

        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Slots busy conductor goal");
        PassVerification(kernel, goal, goal.Tasks.Single());
        var worktree = GoalWorktrees.Ensure(root, goal.Id);
        File.WriteAllText(Path.Combine(worktree, "feature.txt"), "goal work");
        RunGit(worktree, "add", "feature.txt");
        RunGit(worktree, "commit", "-m", "goal work");
        var busy = new DotnetBuildLeaseAcquisition.SlotsBusy(
            "goal-slots-busy",
            [new DotnetBuildStableSlotWait(0, 12345)]);

        var driver = new ConductorDriver(
            kernel,
            workspace,
            new ThrowingAcceptanceVerifier(new DotnetBuildSlotsBusyException(busy)),
            DefaultAgents(),
            WorkerProfileCatalog.Default());

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Contains("slots busy", held.Reason, StringComparison.OrdinalIgnoreCase);
        var journal = GoalOperationJournal.Read(root, goal.Id);
        var blockedOutcome = journal.Entries.LastOrDefault(entry => entry.AcceptanceOutcome == "blocked:slot-unavailable");
        Assert.NotNull(blockedOutcome);
        Assert.Equal(GoalOperationStatus.Failed, blockedOutcome.Status);
        Assert.False(string.IsNullOrWhiteSpace(blockedOutcome.BranchHeadSha));
        Assert.False(string.IsNullOrWhiteSpace(blockedOutcome.MainHeadSha));
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_acceptance_cancellation_probe_stops_reopened_Active_goal")]
    public async Task ConductorDriverAcceptanceCancellationProbeStopsReopenedActiveGoal()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
            var kernel = new AgentOrchestratorKernel();
            var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
                kernel,
                DefaultAgents(),
                "Cancel stale acceptance before retry dispatch");
            var task = goal.Tasks.Single();

            await repository.SaveAsync(kernel);
            Assert.True(ConductorDriver.IsAcceptanceAttemptCancelled(workspace, goal.Id));

            PassVerification(kernel, goal, task);
            await repository.SaveAsync(kernel);
            Assert.False(ConductorDriver.IsAcceptanceAttemptCancelled(workspace, goal.Id));

            kernel.BeginGoalAcceptanceVerification(goal.Id, "Acceptance attempt launched.");
            await repository.SaveAsync(kernel);
            Assert.False(ConductorDriver.IsAcceptanceAttemptCancelled(workspace, goal.Id));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Xunit.Fact(DisplayName = "ConductorDriver composes gate-ready input before invoking the projector")]
    public void ProjectGateReadyCandidateComposesSnapshotInOrder()
    {
        const string branchRevision = "1111111111111111111111111111111111111111";
        const string mainRevision = "2222222222222222222222222222222222222222";
        var (kernel, goal) = SimpleGoal("Gate-ready composition");
        PassVerification(kernel, goal, goal.Tasks.Single());
        var calls = new List<string>();
        var projector = new GateReadyCandidateProjector(
            goalId =>
            {
                calls.Add("revisions");
                Assert.Equal(goal.Id, goalId);
                return new GateReadyCandidateRevisionPair(branchRevision, mainRevision);
            },
            goalId =>
            {
                calls.Add("scope");
                Assert.Equal(goal.Id, goalId);
                return new GateReadyLandingScopeObservation(
                    Succeeded: true,
                    Files: ["src/Mcg.AgentOrchestrator.Core/Feature.cs"]);
            },
            (goalId, observedBranch, observedMain) =>
            {
                calls.Add("merge");
                Assert.Equal(goal.Id, goalId);
                Assert.Equal(branchRevision, observedBranch);
                Assert.Equal(mainRevision, observedMain);
                return new GateReadyMergeTreeObservation(IsClean: true);
            });
        var driver = MakeDriver(
            getFacts: _ =>
            {
                calls.Add("lifecycle");
                return GoalLifecycleFacts.None;
            },
            classifyRisk: _ =>
            {
                calls.Add("risk");
                return ChangeRiskTier.DocsOnly;
            },
            isVerificationGateSatisfied: _ =>
            {
                calls.Add("gate");
                return true;
            },
            gateReadyCandidateProjector: projector);

        var result = driver.ProjectGateReadyCandidate(goal, ConductorAutonomyPolicy.Conservative);

        var projection = Assert.IsType<GateReadyCandidateProjectionResult.Ready>(result).Projection;
        Assert.Equal(goal.Id, projection.GoalId);
        Assert.Equal(GoalLifecycleState.Verified, projection.LifecycleState);
        Assert.Equal(GateReadyVerificationState.Satisfied, projection.VerificationState);
        Assert.Equal(ChangeRiskTier.DocsOnly, projection.ChangeRiskTier);
        Assert.Equal(ConductorTransitionDecision.Auto, projection.AutoPromotionDisposition);
        Assert.Equal(branchRevision, projection.BranchRevision);
        Assert.Equal(mainRevision, projection.MainRevision);
        Assert.Equal(
            ["lifecycle", "gate", "risk", "revisions", "scope", "merge", "revisions"],
            calls);
    }

    [Xunit.Theory(DisplayName = "ConductorDriver maps gate-ready snapshot delegate failures to typed exclusions")]
    [Xunit.InlineData("lifecycle", (int)GateReadyCandidateExclusionReason.LifecycleNotReady, 0)]
    [Xunit.InlineData("gate", (int)GateReadyCandidateExclusionReason.GateNotReady, 0)]
    [Xunit.InlineData("risk", (int)GateReadyCandidateExclusionReason.RiskUnknown, 1)]
    public void ProjectGateReadyCandidateMapsSnapshotDelegateFailures(
        string failingDelegate,
        int expectedReasonValue,
        int expectedRevisionReads)
    {
        var (kernel, goal) = SimpleGoal("Gate-ready delegate failure");
        PassVerification(kernel, goal, goal.Tasks.Single());
        var revisionReads = 0;
        var projector = new GateReadyCandidateProjector(
            _ =>
            {
                revisionReads++;
                return new GateReadyCandidateRevisionPair(
                    "1111111111111111111111111111111111111111",
                    "2222222222222222222222222222222222222222");
            },
            _ => throw new InvalidOperationException("Scope should not be read."),
            (_, _, _) => throw new InvalidOperationException("Merge status should not be read."));
        var driver = MakeDriver(
            getFacts: _ => failingDelegate == "lifecycle"
                ? throw new InvalidOperationException("Lifecycle unavailable.")
                : GoalLifecycleFacts.None,
            classifyRisk: _ => failingDelegate == "risk"
                ? throw new InvalidOperationException("Risk unavailable.")
                : ChangeRiskTier.DocsOnly,
            isVerificationGateSatisfied: _ => failingDelegate == "gate"
                ? throw new InvalidOperationException("Gate unavailable.")
                : true,
            gateReadyCandidateProjector: projector);

        var result = driver.ProjectGateReadyCandidate(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(
            (GateReadyCandidateExclusionReason)expectedReasonValue,
            Assert.IsType<GateReadyCandidateProjectionResult.Excluded>(result).Reason);
        Assert.Equal(expectedRevisionReads, revisionReads);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver fails closed when the gate-ready projector is absent")]
    public void ProjectGateReadyCandidateWithoutProjectorReturnsRevisionUnknown()
    {
        var (kernel, goal) = SimpleGoal("Gate-ready projector missing");
        PassVerification(kernel, goal, goal.Tasks.Single());
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            classifyRisk: _ => ChangeRiskTier.DocsOnly,
            isVerificationGateSatisfied: _ => true,
            gateReadyCandidateProjector: null);

        var result = driver.ProjectGateReadyCandidate(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(
            GateReadyCandidateExclusionReason.RevisionUnknown,
            Assert.IsType<GateReadyCandidateProjectionResult.Excluded>(result).Reason);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_acceptance_slot_path_skips_already_merged_branch_before_lease")]
    public void ConductorDriverAcceptanceSlotPathSkipsAlreadyMergedBranchBeforeLease()
    {
        var root = CreateTempDirectory();
        RunGit(root, "init");
        RunGit(root, "checkout", "-b", "main");
        RunGit(root, "config", "user.email", "test@example.com");
        RunGit(root, "config", "user.name", "Test User");
        File.WriteAllText(Path.Combine(root, "README.md"), "initial");
        RunGit(root, "add", ".");
        RunGit(root, "commit", "-m", "initial");

        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Already merged gate skip");
        PassVerification(kernel, goal, goal.Tasks.Single());
        var worktree = GoalWorktrees.Ensure(root, goal.Id);
        File.WriteAllText(Path.Combine(worktree, "merged-before-gate.txt"), "goal work");
        RunGit(worktree, "add", "merged-before-gate.txt");
        RunGit(worktree, "commit", "-m", "goal work");
        var branchTip = GitCli.Run(worktree, "rev-parse", "HEAD").Output.Trim();
        RunGit(root, "merge", "--ff-only", GoalWorktrees.BranchName(goal.Id));

        var driver = new ConductorDriver(
            kernel,
            workspace,
            new FakeAcceptanceVerifier(),
            DefaultAgents(),
            WorkerProfileCatalog.Default());
        var candidate = driver.TryBuildParallelAcceptanceCandidate(goal, ConductorAutonomyPolicy.Conservative, 0);
        Assert.NotNull(candidate);
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            Path.Combine(root, ".orchestrator", "test-acceptance-attempts"),
            root,
            runInline: true,
            tryRunPreSlot: driver.RunParallelLandingAcceptancePreSlot);
        var acceptanceRan = false;

        var decision = coordinator.Evaluate(
            candidate!,
            ConductorAutonomyPolicy.Conservative,
            (_, _, lease, _) =>
            {
                acceptanceRan = true;
                Assert.NotNull(lease);
                return ConductorParallelAcceptanceRunResult.Accepted(
                    candidate!,
                    AcceptanceVerificationSummary.PassedWithNoUnmetCriteria);
            });

        Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Completed, decision.Kind);
        Assert.False(acceptanceRan);
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed, decision.Attempt.Outcome);
        Assert.Empty(decision.Attempt.LeaseReceipts ?? []);
        Assert.NotNull(decision.Run?.EarlyResult);
        Assert.Equal("skip-already-merged", decision.Run!.EarlyOutcome?.Kind);
        var journal = GoalOperationJournal.Read(root, goal.Id);
        var skip = Assert.Single(journal.Entries.Where(entry => entry.AcceptanceOutcome == "skip-already-merged"));
        Assert.Equal(GoalOperationStatus.Skipped, skip.Status);
        Assert.Contains($"mergeCommitSha={branchTip}", skip.Detail, StringComparison.Ordinal);

        GoalOperationJournal.RecordLandingIntent(
            root,
            goal,
            GoalWorktrees.BranchName(goal.Id),
            LandingExecutor.IntegrationBranchName,
            branchTip,
            "test");
        var secondSkip = driver.RunParallelLandingAcceptancePreSlot(candidate!, ConductorAutonomyPolicy.Conservative);
        Assert.NotNull(secondSkip);
        Assert.Equal("skip-already-merged", secondSkip!.EarlyOutcome?.Kind);
    }

    [Xunit.Fact]
    public void PreReviewAttempt_FocusedKind_SkipsLandingPreSlot()
    {
        var root = CreateTempDirectory();
        try
        {
            var (_, goal) = SoftwareGoal();
            var candidate = ConductorParallelAcceptanceCandidate.Create(
                goal,
                slotIndex: 0,
                fileScopes: [],
                branchHeadSha: "focused-sha",
                mainHeadSha: null);
            var preSlotRuns = 0;
            var focusedRuns = 0;
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(root, "attempts"),
                runInline: true,
                tryRunPreSlot: (_, _) =>
                {
                    preSlotRuns++;
                    return ConductorParallelAcceptanceRunResult.Early(
                        candidate,
                        new ConductorAdvanceResult(
                            goal.Id.Value,
                            goal.Id.Value[..8],
                            ConductorAutonomyPolicy.Permissive.Name,
                            new ConductorAdvanceOutcome.Done(GoalLifecycleState.Verified)));
                },
                acquireStableSlotLease: (_, _) => null);

            var decision = coordinator.EvaluateFocusedEvidence(
                candidate,
                ConductorAutonomyPolicy.Permissive,
                "Infrastructure.Tests: FocusedTests",
                (_, request, _, _) =>
                {
                    focusedRuns++;
                    return PassingPreReviewEvidence(request);
                });

            Assert.Equal(0, preSlotRuns);
            Assert.Equal(1, focusedRuns);
            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Completed, decision.Kind);
            Assert.NotNull(decision.Run?.FocusedEvidence);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_empty_batch_surfaces_operator_approval_reasons")]
    public void ConductorDriverEmptyBatchSurfacesOperatorApprovalReasons()
    {
        var plan = new ParallelExecutionPlan(
            [],
            [
                new ParallelExecutionDecision(
                    "task-1",
                    ParallelExecutionDisposition.RequiresOperatorApproval,
                    null,
                    ["high-risk ownership area requires operator approval: Script scripts/Invoke-TestSummary.ps1"])
            ]);

        var reason = ConductorDriver.DescribeEmptyBatch(plan);

        Assert.True(reason.Contains("require operator approval", StringComparison.OrdinalIgnoreCase));
        Assert.True(reason.Contains("scripts/Invoke-TestSummary.ps1", StringComparison.Ordinal));
        Assert.False(reason.Contains("no assigned or ready tasks", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_empty_batch_surfaces_ready_blocked_diagnostics_before_generic_reason")]
    public void ConductorDriverEmptyBatchSurfacesReadyBlockedDiagnosticsBeforeGenericReason()
    {
        var taskId = TaskId.New().Value;
        var plan = new ParallelExecutionPlan([], []);
        var diagnostic = new ReadyBlockedDiagnostic(
            "abc12345",
            1,
            taskId,
            "codex-cli",
            "dirty-worktree",
            ["blocked: worktree has 1 uncommitted change(s) before dispatch; paths=[src/dirty.cs]"]);

        var reason = ConductorDriver.DescribeEmptyBatch(plan, [diagnostic]);

        Assert.Contains("assigned tasks were excluded", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(taskId, reason, StringComparison.Ordinal);
        Assert.Contains("codex-cli", reason, StringComparison.Ordinal);
        Assert.Contains("dirty-worktree", reason, StringComparison.Ordinal);
        Assert.Contains("worktree has 1 uncommitted change", reason, StringComparison.Ordinal);
        Assert.Contains("src/dirty.cs", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("no assigned or ready tasks", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_empty_batch_without_approval_blocks_uses_generic_reason")]
    public void ConductorDriverEmptyBatchWithoutApprovalBlocksUsesGenericReason()
    {
        var plan = new ParallelExecutionPlan([], []);

        var reason = ConductorDriver.DescribeEmptyBatch(plan);

        Assert.True(reason.Contains("no assigned or ready tasks", StringComparison.Ordinal));
    }

    // ── Created state ─────────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_Created_creates_workspace_and_returns_Executed")]
    public void ConductorDriverCreatedCreatesWorkspaceAndReturnsExecuted()
    {
        var (_, goal) = SimpleGoal();
        var workspaceCreated = false;

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            createWorkspace: _ => { workspaceCreated = true; return "/tmp/workspace"; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(workspaceCreated);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Equal(GoalLifecycleState.Created, ((ConductorAdvanceOutcome.Executed)result.Outcome).FromState);
    }

    // ── WorkspaceReady state ──────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_WorkspaceReady_at_cap_returns_Held")]
    public void ConductorDriverWorkspaceReadyAtCapReturnsHeld()
    {
        var (_, goal) = SimpleGoal();
        var policy = ConductorAutonomyPolicy.Conservative; // MaxConcurrentPaidWorkers = 4
        var dispatchCalled = false;

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => policy.MaxConcurrentPaidWorkers, // at cap
            dispatchAndStart: _ => { dispatchCalled = true; return DispatchStartOutcome.Started(); });

        var result = driver.AdvanceOnce(goal, policy);

        Assert.False(dispatchCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Held);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_WorkspaceReady_under_cap_dispatches_and_returns_Executed")]
    public void ConductorDriverWorkspaceReadyUnderCapDispatchesAndReturnsExecuted()
    {
        var (_, goal) = SimpleGoal();
        var dispatchCalled = false;

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => 0,
            dispatchAndStart: _ => { dispatchCalled = true; return DispatchStartOutcome.Started(); });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(dispatchCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Equal(GoalLifecycleState.WorkspaceReady, ((ConductorAdvanceOutcome.Executed)result.Outcome).FromState);
    }

    [Xunit.Theory(DisplayName = "ConductorDriver_worker_admission_snapshot_clamps_above_capacity")]
    [Xunit.InlineData(false, 0)]
    [Xunit.InlineData(true, 1)]
    public void ConductorDriverWorkerAdmissionSnapshotClampsAboveCapacity(bool gateReady, int expectedReservedSlots)
    {
        var policy = ConductorAutonomyPolicy.Conservative with
        {
            MaxConcurrentPaidWorkers = ConductorBatchLoop.WorkerAdmissionCapacity + 3
        };
        var driver = MakeDriver(hasGateReadyGoal: () => gateReady);

        var snapshot = driver.GetWorkerAdmissionSnapshot(policy);

        Assert.Equal(policy.MaxConcurrentPaidWorkers, snapshot.ConfiguredWorkerCap);
        Assert.Equal(ConductorBatchLoop.WorkerAdmissionCapacity, snapshot.AdmissionCapacity);
        Assert.Equal(expectedReservedSlots, snapshot.ReservedGateSlots);
        Assert.Equal(ConductorBatchLoop.WorkerAdmissionCapacity - expectedReservedSlots, snapshot.EffectiveWorkerCap);
        Assert.True(snapshot.EffectiveWorkerCap < snapshot.ConfiguredWorkerCap);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_WorkspaceReady_capacity_clamp_reports_effective_cap_and_deferral")]
    public void ConductorDriverWorkspaceReadyCapacityClampReportsEffectiveCapAndDeferral()
    {
        var (_, goal) = SimpleGoal();
        var policy = ConductorAutonomyPolicy.Conservative with
        {
            MaxConcurrentPaidWorkers = ConductorBatchLoop.WorkerAdmissionCapacity + 3
        };
        var dispatchCalled = false;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => ConductorBatchLoop.WorkerAdmissionCapacity,
            dispatchAndStart: _ => { dispatchCalled = true; return DispatchStartOutcome.Started(); },
            hasGateReadyGoal: () => false);

        ConductorAdvanceResult? result = null;
        var output = AsyncLocalConsoleRouter.Capture(() => result = driver.AdvanceOnce(goal, policy));

        Assert.False(dispatchCalled);
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result!.Outcome);
        Assert.Contains("worker admission capacity (9/9)", held.Reason, StringComparison.Ordinal);
        Assert.Contains("configured cap 12 is clamped", held.Reason, StringComparison.Ordinal);
        Assert.Contains("ADMISSION", output, StringComparison.Ordinal);
        Assert.Contains("reason=worker-admission-capacity", output, StringComparison.Ordinal);
        Assert.Contains("cap=9", output, StringComparison.Ordinal);
        Assert.Contains("configuredCap=12", output, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_WorkspaceReady_reserves_acceptance_capacity_not_build_permit_count")]
    public void ConductorDriverWorkspaceReadyReservesAcceptanceCapacityNotBuildPermitCount()
    {
        var (_, goal) = SimpleGoal();
        var policy = ConductorAutonomyPolicy.Conservative with
        {
            MaxConcurrentPaidWorkers = ConductorBatchLoop.WorkerAdmissionCapacity
        };
        var dispatchCalled = false;

        // The gate reservation draws from the paid-worker ADMISSION pool (WorkerAdmissionCapacity),
        // NOT the parallel-acceptance width / build-permit count. At WorkerAdmissionCapacity - 1
        // running with a ready gate, the reserved slot holds the next dispatch.
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => ConductorBatchLoop.WorkerAdmissionCapacity - 1,
            dispatchAndStart: _ => { dispatchCalled = true; return DispatchStartOutcome.Started(); },
            hasGateReadyGoal: () => true);

        ConductorAdvanceResult? result = null;
        var output = AsyncLocalConsoleRouter.Capture(() => result = driver.AdvanceOnce(goal, policy));

        Assert.False(dispatchCalled);
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result!.Outcome);
        Assert.Equal(GoalLifecycleState.WorkspaceReady, held.State);
        Assert.Contains("gate-ready goal reserving a stable slot", held.Reason, StringComparison.Ordinal);
        Assert.Contains("ADMISSION", output, StringComparison.Ordinal);
        Assert.Contains("reason=reserved-gate-slot", output, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_gate_reservation_draws_from_worker_admission_not_acceptance_width")]
    public void ConductorDriverGateReservationDrawsFromWorkerAdmissionNotAcceptanceWidth()
    {
        // Decoupling guard: parallel-acceptance WIDTH stays aligned with build concurrency (2)
        // while the paid-worker ADMISSION pool the gate reservation draws from is independent (9).
        // These are two distinct concepts and must not share one constant.
        Assert.Equal(
            DotnetBuildEnvironmentManager.BuildConcurrencySlotCount,
            ConductorBatchLoop.DefaultParallelAcceptanceCapacity);
        Assert.Equal(2, ConductorBatchLoop.DefaultParallelAcceptanceCapacity);
        Assert.Equal(9, ConductorBatchLoop.WorkerAdmissionCapacity);
        Assert.NotEqual(
            ConductorBatchLoop.DefaultParallelAcceptanceCapacity,
            ConductorBatchLoop.WorkerAdmissionCapacity);

        var (_, goal) = SimpleGoal();
        var policy = ConductorAutonomyPolicy.Conservative with
        {
            MaxConcurrentPaidWorkers = ConductorBatchLoop.WorkerAdmissionCapacity
        };
        var dispatchCalled = false;

        // Paid workers already running == acceptance width (2) while a gate is ready. If the
        // reservation were (wrongly) drawn from acceptance width, the cap would collapse to
        // min(9, 2 - 1) = 1 and admission would be held. Decoupled from worker admission the
        // cap is min(9, 9 - 1) = 8, so the third worker is still admitted during the gate.
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => ConductorBatchLoop.DefaultParallelAcceptanceCapacity,
            dispatchAndStart: _ => { dispatchCalled = true; return DispatchStartOutcome.Started(); },
            hasGateReadyGoal: () => true);

        var result = driver.AdvanceOnce(goal, policy);

        Assert.True(dispatchCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Equal(
            GoalLifecycleState.WorkspaceReady,
            ((ConductorAdvanceOutcome.Executed)result.Outcome).FromState);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_repairs_terminal_goal_with_assigned_task_before_acceptance")]
    public void ConductorDriverRepairsTerminalGoalWithAssignedTaskBeforeAcceptance()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "Retry desync");
        var snapshot = kernel.ExportSnapshot();
        var badGoalSnapshot = snapshot.Goals.Single() with
        {
            Status = GoalStatus.Completed,
            Tasks = snapshot.Goals.Single().Tasks
                .Select(task => task with { Status = WorkTaskStatus.Assigned, LastVerification = null })
                .ToArray()
        };
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with { Goals = [badGoalSnapshot] });
        goal = kernel.Goals.Single();
        var dispatched = false;
        var acceptanceCalled = false;

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ =>
            {
                dispatched = true;
                return DispatchStartOutcome.Started();
            },
            runAcceptance: _ =>
            {
                acceptanceCalled = true;
                return true;
            },
            normalizeLifecycleState: (g, reason) => kernel.NormalizeGoalLifecycleState(g.Id, reason));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(dispatched);
        Assert.False(acceptanceCalled);
        Assert.Equal(GoalStatus.Active, goal.Status);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Equal(GoalLifecycleState.WorkspaceReady, ((ConductorAdvanceOutcome.Executed)result.Outcome).FromState);
        Assert.Contains(goal.Timeline, evt =>
            evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.Contains("Conductor auto-repaired", StringComparison.Ordinal));
    }

    // ── Dispatched state ──────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_Dispatched_starts_recorded_dispatch")]
    public void ConductorDriverDispatchedStartsRecordedDispatch()
    {
        var (kernel, goal) = SimpleGoal();
        DispatchTask(kernel, goal, goal.Tasks.Single());
        var startCalled = false;

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            startRecordedDispatches: _ =>
            {
                startCalled = true;
                return DispatchStartOutcome.Started();
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(startCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Equal(GoalLifecycleState.Dispatched, ((ConductorAdvanceOutcome.Executed)result.Outcome).FromState);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Dispatched_start_failure_escalates_with_reason")]
    public void ConductorDriverDispatchedStartFailureEscalatesWithReason()
    {
        var (kernel, goal) = SimpleGoal();
        DispatchTask(kernel, goal, goal.Tasks.Single());
        var callCount = 0;
        var shutdownCalled = false;
        string? escalationReason = null;
        var phaseTimings = new List<string>();
        const string startFailReason = "Recorded dispatch start failed: worker command refused to launch";

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            startRecordedDispatches: _ =>
            {
                callCount++;
                return DispatchStartOutcome.SpawnFailed(startFailReason);
            },
            buildServerShutdown: () => { shutdownCalled = true; },
            writeEscalationWithResult: (_, _, reason) =>
            {
                escalationReason = reason;
                return new LandingEscalationWriteResult(2, "ok", 25, "timeout", 3, "error");
            });
        driver.PhaseTimingSink = phaseTimings.Add;

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(2, callCount);
        Assert.True(shutdownCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
        Assert.Equal(GoalLifecycleState.Dispatched, ((ConductorAdvanceOutcome.Escalated)result.Outcome).State);
        Assert.Equal(startFailReason, escalationReason);
        var remediationTiming = Assert.Single(
            phaseTimings,
            line => line.Contains("phase=dispatch-remediation", StringComparison.Ordinal));
        Assert.Contains("result=ran", remediationTiming, StringComparison.Ordinal);
        Assert.Matches(@"elapsed_ms=[1-9]\d*", remediationTiming);
        Assert.Contains(
            phaseTimings,
            line => line.Contains("phase=escalation-write", StringComparison.Ordinal) &&
                    line.Contains("json_ms=2 json=ok", StringComparison.Ordinal) &&
                    line.Contains("collab_ms=25 collab=timeout", StringComparison.Ordinal) &&
                    line.Contains("channel_ms=3 channel=error", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_dispatch_remediation_runs_once_per_tick_and_rearms")]
    public void ConductorDriverDispatchRemediationRunsOncePerTickAndRearms()
    {
        var (_, firstGoal) = SimpleGoal("First failing dispatch");
        var (_, secondGoal) = SimpleGoal("Second failing dispatch");
        var shutdownCalls = 0;
        var phaseTimings = new List<string>();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ => DispatchStartOutcome.SpawnFailed("spawn failed"),
            buildServerShutdown: () => shutdownCalls++);
        driver.PhaseTimingSink = phaseTimings.Add;

        driver.BeginTick();
        driver.AdvanceOnce(firstGoal, ConductorAutonomyPolicy.Conservative);
        driver.AdvanceOnce(secondGoal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(1, shutdownCalls);
        var firstTickResults = phaseTimings
            .Where(line => line.Contains("phase=dispatch-remediation", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, firstTickResults.Length);
        Assert.Contains("result=ran", firstTickResults[0], StringComparison.Ordinal);
        Assert.Contains("result=skipped-tick-latch", firstTickResults[1], StringComparison.Ordinal);

        driver.BeginTick();
        driver.AdvanceOnce(firstGoal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(2, shutdownCalls);
        Assert.Contains(
            phaseTimings.Skip(firstTickResults.Length),
            line => line.Contains("phase=dispatch-remediation", StringComparison.Ordinal) &&
                    line.Contains("result=ran", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_dispatch_remediation_timeout_is_bounded_and_preserves_failure")]
    public void ConductorDriverDispatchRemediationTimeoutIsBoundedAndPreservesFailure()
    {
        var (_, goal) = SimpleGoal("Timed out remediation");
        using var shutdownEntered = new ManualResetEventSlim();
        using var releaseShutdown = new ManualResetEventSlim();
        var phaseTimings = new List<string>();
        var startCalls = 0;
        const string spawnFailReason = "worker spawn failed with access denied";
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ => ++startCalls == 1
                ? DispatchStartOutcome.SpawnFailed(spawnFailReason)
                : DispatchStartOutcome.EmptyBatch("retry started no processes"),
            buildServerShutdown: () =>
            {
                shutdownEntered.Set();
                releaseShutdown.Wait();
            },
            buildServerShutdownTimeout: TimeSpan.FromMilliseconds(25));
        driver.PhaseTimingSink = phaseTimings.Add;

        try
        {
            var advance = Task.Run(() =>
                driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative));

            Assert.True(shutdownEntered.Wait(TimeSpan.FromSeconds(1)));
            Assert.True(advance.Wait(TimeSpan.FromSeconds(1)));
            var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(advance.Result.Outcome);

            Assert.Equal(spawnFailReason, escalated.Reason);
            Assert.Contains(
                phaseTimings,
                line => line.Contains("phase=dispatch-remediation", StringComparison.Ordinal) &&
                        line.Contains("result=timeout", StringComparison.Ordinal));
        }
        finally
        {
            releaseShutdown.Set();
        }
    }

    // ── Running state ─────────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_Running_returns_Held")]
    public void ConductorDriverRunningReturnsHeld()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        DispatchTask(kernel, goal, task);
        var process = new TaskProcessRecord(12345, "test.exe", "C:\\tmp",
            "C:\\tmp\\stdout", "C:\\tmp\\stderr", "C:\\tmp\\exit",
            StartedAt: DateTimeOffset.UtcNow, CompletedAt: null, ExitCode: null);
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);

        var driver = MakeDriver(getFacts: _ => GoalLifecycleFacts.None);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(result.Outcome is ConductorAdvanceOutcome.Held);
        Assert.Equal(GoalLifecycleState.Running, ((ConductorAdvanceOutcome.Held)result.Outcome).State);
    }

    // ── AwaitingVerification state ────────────────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_AwaitingVerification_returns_Held")]
    public void ConductorDriverAwaitingVerificationReturnsHeld()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        // Mark task Completed without verification → MissingVerification gate keeps goal Active,
        // all tasks Completed → GoalLifecycleState.AwaitingVerification.
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "done");

        var driver = MakeDriver(getFacts: _ => GoalLifecycleFacts.None);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(result.Outcome is ConductorAdvanceOutcome.Held);
        Assert.Equal(GoalLifecycleState.AwaitingVerification, ((ConductorAdvanceOutcome.Held)result.Outcome).State);
    }

    // ── Verified state ────────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_acceptance_fails_escalates")]
    public void ConductorDriverVerifiedAcceptanceFailsEscalates()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var escalated = false;

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptance: _ => false,
            writeEscalation: (_, _, _) => { escalated = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(escalated);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
        Assert.Equal(GoalLifecycleState.Verified, ((ConductorAdvanceOutcome.Escalated)result.Outcome).State);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_rebase_conflict_escalates_before_acceptance")]
    public void ConductorDriverVerifiedRebaseConflictEscalatesBeforeAcceptance()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var acceptanceCalled = false;
        string? escalationReason = null;

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            rebaseOntoMain: _ => new GoalWorktreeRebaseResult(
                GoalWorktreeRebaseStatus.Conflict, "goal/test", "conflict", ["src/Foo.cs"], "workspace rebase"),
            runAcceptance: _ => { acceptanceCalled = true; return true; },
            writeEscalation: (_, _, reason) => { escalationReason = reason; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        // Rebase-before-acceptance: an un-integrable branch escalates without spending an acceptance run,
        // and acceptance never verifies the pre-integration branch.
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
        Assert.False(acceptanceCalled);
        Assert.Contains("pre-landing rebase conflict", escalationReason!, StringComparison.Ordinal);
        Assert.Contains("src/Foo.cs", escalationReason!, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_advisory_only_unmet_acceptance_criteria_land_with_task_note")]
    public void ConductorDriverVerifiedAdvisoryOnlyUnmetAcceptanceCriteriaLandWithTaskNote()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        PassVerification(kernel, goal, task);
        kernel.RecordCriterionRetryFeedback(goal.Id, task.Id, ["stale required retry feedback"]);
        var landCalled = false;
        var retryCalled = false;
        var advisory = new AcceptanceCheckResult(
            "test tamper guard",
            false,
            0,
            "1 test degradation signal(s)",
            ResultSummary: "1 test degradation signal(s)",
            Advisory: true);

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => new AcceptanceVerificationSummary(true, [advisory]),
            retryTask: (_, _, _) =>
            {
                retryCalled = true;
                throw new InvalidOperationException("Advisory acceptance criteria must not retry tasks.");
            },
            recordTaskNote: (goalId, taskId, message) => kernel.RecordTaskNote(goalId, taskId, message),
            clearCriterionRetryFeedback: kernel.ClearCriterionRetryFeedback,
            classifyRisk: _ => ChangeRiskTier.DocsOnly,
            land: g =>
            {
                landCalled = true;
                return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed");
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.False(retryCalled);
        Assert.True(landCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Assert.Equal(1, task.CriterionRetryCount);
        Assert.Empty(task.CriterionRetryFeedback);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == task.Id &&
            evt.Kind == ProgressKind.TaskNote &&
            evt.Message.Contains("Advisory acceptance criteria observed during landing (non-gating)", StringComparison.Ordinal) &&
            evt.Message.Contains("test tamper guard: 1 test degradation signal(s)", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_required_unmet_acceptance_criterion_retries_task_with_feedback")]
    public void ConductorDriverVerifiedRequiredUnmetAcceptanceCriterionRetriesTaskWithFeedback()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        PassVerification(kernel, goal, task);
        var landCalled = false;
        var retryCalled = false;
        string? retryMessage = null;
        var unmet = new AcceptanceCheckResult(
            "command-exit dotnet test --filter RetryEvidence",
            false,
            1,
            string.Join(Environment.NewLine,
            [
                "src/Foo.cs(12,34): error CS1002: ; expected",
                "[xUnit.net 00:00:01.23]     Mcg.AgentOrchestrator.Tests.RetryEvidenceTests.IncludesFailures [FAIL]",
            ]),
            ResultSummary: "focused conductor tests failed");

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => new AcceptanceVerificationSummary(true, [unmet]),
            retryTask: (goalId, taskId, message) =>
            {
                retryCalled = true;
                retryMessage = message;
                return kernel.RetryTask(goalId, taskId, message);
            },
            recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
            land: g =>
            {
                landCalled = true;
                return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed");
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);
        var brief = kernel.BuildTaskBrief(goal.Id, task.Id);

        Assert.True(retryCalled);
        Assert.False(landCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.Equal(1, task.CriterionRetryCount);
        Assert.Contains("failed check: command-exit dotnet test --filter RetryEvidence (exit code 1)", retryMessage!, StringComparison.Ordinal);
        Assert.Contains("src/Foo.cs(12,34): error CS1002: ; expected", retryMessage!, StringComparison.Ordinal);
        Assert.Contains("Mcg.AgentOrchestrator.Tests.RetryEvidenceTests.IncludesFailures [FAIL]", retryMessage!, StringComparison.Ordinal);
        Assert.True(task.CriterionRetryFeedback.Any(item => item.Contains("src/Foo.cs(12,34): error CS1002: ; expected", StringComparison.Ordinal)));
        Assert.True(task.CriterionRetryFeedback.Any(item => item.Contains("Mcg.AgentOrchestrator.Tests.RetryEvidenceTests.IncludesFailures [FAIL]", StringComparison.Ordinal)));
        Assert.Contains("## Unmet acceptance criteria from the prior attempt - fix these:", brief.Content, StringComparison.Ordinal);
        Assert.Contains("src/Foo.cs(12,34): error CS1002: ; expected", brief.Content, StringComparison.Ordinal);
        Assert.Contains("Mcg.AgentOrchestrator.Tests.RetryEvidenceTests.IncludesFailures [FAIL]", brief.Content, StringComparison.Ordinal);
        Assert.Contains("focused conductor tests failed", brief.Content, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_retry_feedback_uses_complete_trx_receipt_without_changing_retry_state")]
    public void ConductorDriverRetryFeedbackUsesCompleteTrxReceiptWithoutChangingRetryState()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        PassVerification(kernel, goal, task);
        var trxPath = Path.Combine(
            InfrastructureTestSupport.FindRepositoryRoot(),
            "tests",
            "Mcg.AgentOrchestrator.Infrastructure.Tests",
            "TestData",
            "Fixtures",
            "mtp-xunit-v3-failures.trx.xml");
        string? retryMessage = null;
        var landCalled = false;
        var unmet = new AcceptanceCheckResult(
            "infrastructure tests: retry evidence",
            false,
            1,
            "[FAIL] GoalAcceptanceVerifier: Assert.Contains() Failure: Sub-string not found",
            ResultSummary: "infrastructure tests failed",
            TestResultPaths: [trxPath]);
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => new AcceptanceVerificationSummary(true, [unmet]),
            retryTask: (goalId, taskId, message) =>
            {
                retryMessage = message;
                return kernel.RetryTask(goalId, taskId, message);
            },
            recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
            land: g =>
            {
                landCalled = true;
                return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed");
            });

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);
        var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

        Assert.False(landCalled);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.Equal(1, task.CriterionRetryCount);
        Assert.Contains(trxPath, retryMessage!, StringComparison.Ordinal);
        Assert.Contains(
            "[FAIL] Mcg.AgentOrchestrator.Infrastructure.Tests.RetryEvidenceTests.IncludesFailures (Failed)",
            retryMessage!,
            StringComparison.Ordinal);
        Assert.Contains("Expected: 2", retryMessage!, StringComparison.Ordinal);
        Assert.Contains("Actual:   0", retryMessage!, StringComparison.Ordinal);
        Assert.Contains("RetryEvidenceTests.cs:line 42", retryMessage!, StringComparison.Ordinal);
        Assert.Contains("TimeoutTests.ReportsDuration (Timeout)", retryMessage!, StringComparison.Ordinal);
        Assert.Contains("Expected: 2", brief, StringComparison.Ordinal);
        Assert.Contains("Actual:   0", brief, StringComparison.Ordinal);
        Assert.Contains("RetryEvidenceTests.cs:line 42", brief, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_retry_feedback_drops_whole_trx_entries_and_names_receipt")]
    public void ConductorDriverRetryFeedbackDropsWholeTrxEntriesAndNamesReceipt()
    {
        var tempDirectory = CreateTempDirectory();
        var trxPath = Path.Combine(tempDirectory, "many-failures.trx");
        try
        {
            new XDocument(
                new XElement(
                    "TestRun",
                    new XElement(
                        "Results",
                        Enumerable.Range(1, 32).Select(index =>
                            new XElement(
                                "UnitTestResult",
                                new XAttribute("testName", $"Example.Tests.CompleteTestName{index:D2}"),
                                new XAttribute("outcome", "Failed"),
                                new XElement(
                                    "Output",
                                    new XElement(
                                        "ErrorInfo",
                                        new XElement("Message", $"complete-message-{index:D2}-end"),
                                        new XElement("StackTrace", $"complete-stack-{index:D2}-end"))))))))
                .Save(trxPath);
            var (kernel, goal) = SimpleGoal();
            var task = goal.Tasks.Single();
            PassVerification(kernel, goal, task);
            string? retryMessage = null;
            var unmet = new AcceptanceCheckResult(
                "infrastructure tests: many failures",
                false,
                1,
                "[FAIL] Example.Tests.CompleteTestName",
                TestResultPaths: [trxPath]);
            var driver = MakeDriver(
                getFacts: _ => GoalLifecycleFacts.None,
                runAcceptanceSummary: _ => new AcceptanceVerificationSummary(true, [unmet]),
                retryTask: (goalId, taskId, message) =>
                {
                    retryMessage = message;
                    return kernel.RetryTask(goalId, taskId, message);
                },
                recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback);

            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            Assert.Contains("Example.Tests.CompleteTestName30 (Failed)", retryMessage!, StringComparison.Ordinal);
            Assert.Contains("complete-message-30-end", retryMessage!, StringComparison.Ordinal);
            Assert.Contains("complete-stack-30-end", retryMessage!, StringComparison.Ordinal);
            Assert.DoesNotContain("Example.Tests.CompleteTestName31", retryMessage!, StringComparison.Ordinal);
            Assert.Contains($"2 more failures omitted — see {trxPath}.", retryMessage!, StringComparison.Ordinal);
            Assert.DoesNotContain("truncated", retryMessage!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_retry_feedback_names_missing_trx_and_keeps_summary")]
    public void ConductorDriverRetryFeedbackNamesMissingTrxAndKeepsSummary()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        PassVerification(kernel, goal, task);
        var missingPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.trx");
        string? retryMessage = null;
        var unmet = new AcceptanceCheckResult(
            "infrastructure tests: missing receipt",
            false,
            1,
            "[FAIL] Summary.Name: Values differ",
            TestResultPaths: [missingPath]);
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => new AcceptanceVerificationSummary(true, [unmet]),
            retryTask: (goalId, taskId, message) =>
            {
                retryMessage = message;
                return kernel.RetryTask(goalId, taskId, message);
            },
            recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback);

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Contains("[FAIL] Summary.Name: Values differ", retryMessage!, StringComparison.Ordinal);
        Assert.Contains($"detail unavailable: no TRX exists for partition \"infrastructure tests: missing receipt\" at {missingPath}", retryMessage!, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_gate_environment_interference_regates_without_developer_retry")]
    public void ConductorDriverGateEnvironmentInterferenceRegatesWithoutDeveloperRetry()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        PassVerification(kernel, goal, task);
        var retryCalled = false;
        var landCalled = false;
        var interference = new AcceptanceCheckResult(
            "structural test coverage: core tests",
            false,
            1,
            "classification: gate-environment-interference\nstructural coverage failed: discovered=615, executed=0, missing=615, emptyPartitions=1",
            ResultSummary: "structural coverage failed: discovered=615, executed=0, missing=615, emptyPartitions=1",
            FailureClassification: AcceptanceFailureClassifications.GateEnvironmentInterference);

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => new AcceptanceVerificationSummary(false, [interference]),
            retryTask: (goalId, taskId, message) =>
            {
                retryCalled = true;
                return kernel.RetryTask(goalId, taskId, message);
            },
            land: g =>
            {
                landCalled = true;
                return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed");
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        var held = Xunit.Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Xunit.Assert.Equal(GoalLifecycleState.Verified, held.State);
        Xunit.Assert.Contains("re-gate", held.Reason, StringComparison.Ordinal);
        Xunit.Assert.False(retryCalled);
        Xunit.Assert.False(landCalled);
        Xunit.Assert.Equal(WorkTaskStatus.Completed, task.Status);
        Xunit.Assert.Equal(0, task.CriterionRetryCount);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_unmet_acceptance_retry_feedback_caps_concrete_evidence")]
    public void ConductorDriverVerifiedUnmetAcceptanceRetryFeedbackCapsConcreteEvidence()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        PassVerification(kernel, goal, task);
        string? retryMessage = null;
        var output = string.Join(Environment.NewLine,
            Enumerable.Range(1, 35).Select(index => $"src/Foo{index}.cs({index},1): error CS1002: ; expected"));
        var unmet = new AcceptanceCheckResult(
            "command-exit dotnet test --filter ManyFailures",
            false,
            1,
            output,
            ResultSummary: "many compiler errors");

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => new AcceptanceVerificationSummary(true, [unmet]),
            retryTask: (goalId, taskId, message) =>
            {
                retryMessage = message;
                return kernel.RetryTask(goalId, taskId, message);
            },
            recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback);

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Contains("src/Foo30.cs(30,1): error CS1002: ; expected", retryMessage!, StringComparison.Ordinal);
        Assert.DoesNotContain("src/Foo31.cs(31,1): error CS1002: ; expected", retryMessage!, StringComparison.Ordinal);
        Assert.Contains("5 more acceptance evidence entries omitted.", retryMessage!, StringComparison.Ordinal);
        Assert.True(task.CriterionRetryFeedback.Any(item => item.Contains("5 more acceptance evidence entries omitted.", StringComparison.Ordinal)));
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_unmet_acceptance_retry_feedback_preserves_no_evidence_fallback")]
    public void ConductorDriverVerifiedUnmetAcceptanceRetryFeedbackPreservesNoEvidenceFallback()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        PassVerification(kernel, goal, task);
        string? retryMessage = null;
        var unmet = new AcceptanceCheckResult(
            "grep-present docs/usage.md contains Ready",
            false,
            1,
            "Pattern 'Ready' was not found.",
            ResultSummary: "docs/usage.md is missing Ready");

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => new AcceptanceVerificationSummary(true, [unmet]),
            retryTask: (goalId, taskId, message) =>
            {
                retryMessage = message;
                return kernel.RetryTask(goalId, taskId, message);
            },
            recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback);

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Contains("docs/usage.md is missing Ready", retryMessage!, StringComparison.Ordinal);
        Assert.DoesNotContain("Concrete acceptance failure evidence", retryMessage!, StringComparison.Ordinal);
        Assert.Equal(["grep-present docs/usage.md contains Ready: docs/usage.md is missing Ready"], task.CriterionRetryFeedback);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_unmet_acceptance_criterion_escalates_after_retry_budget")]
    public void ConductorDriverVerifiedUnmetAcceptanceCriterionEscalatesAfterRetryBudget()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        PassVerification(kernel, goal, task);
        kernel.RecordCriterionRetryFeedback(goal.Id, task.Id, ["previous unmet criterion"]);
        var landCalled = false;
        string? escalationReason = null;
        var unmet = new AcceptanceCheckResult(
            "file-exists docs/usage.md",
            false,
            1,
            "file missing",
            ResultSummary: "docs/usage.md missing");

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => new AcceptanceVerificationSummary(true, [unmet]),
            retryTask: (_, _, _) => throw new InvalidOperationException("Retry should not be called after budget is spent."),
            land: g =>
            {
                landCalled = true;
                return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed");
            },
            writeEscalation: (_, _, reason) => { escalationReason = reason; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.False(landCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
        Assert.Contains("Acceptance criteria unmet after 1 retries", escalationReason!, StringComparison.Ordinal);
        Assert.Contains("docs/usage.md missing", escalationReason!, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_acceptance_retry_with_cancelled_task_runs_acceptance")]
    public void ConductorDriverVerifiedAcceptanceRetryWithCancelledTaskRunsAcceptance()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        PassVerification(kernel, goal, task);
        Assert.True(kernel.BeginGoalAcceptanceVerification(goal.Id, "Run acceptance."));
        Assert.True(kernel.ReconcileGoalAcceptanceFailed(
            goal.Id,
            ["environment failure"],
            "Acceptance environment failed."));
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Cancelled, "Operator deliberately descoped task.");
        kernel.RetryAcceptanceGate(goal.Id, "Environment repaired.");
        var acceptanceCalled = false;

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ =>
            {
                acceptanceCalled = true;
                return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
            },
            land: g => new LandingResult(
                g.Id.Value,
                g.Id.Value[..8],
                new LandingDecision.Promote(),
                "integration",
                true,
                "Landed"));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(acceptanceCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Equal(WorkTaskStatus.Cancelled, task.Status);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_all_acceptance_criteria_met_lands")]
    public void ConductorDriverVerifiedAllAcceptanceCriteriaMetLands()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        PassVerification(kernel, goal, task);
        kernel.RecordCriterionRetryFeedback(goal.Id, task.Id, ["stale retry feedback"]);
        var landCalled = false;

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            clearCriterionRetryFeedback: kernel.ClearCriterionRetryFeedback,
            classifyRisk: _ => ChangeRiskTier.DocsOnly,
            land: g =>
            {
                landCalled = true;
                return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed");
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(landCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Equal(0, task.CriterionRetryFeedback.Count);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_runs_semantic_acceptance_after_acceptance_and_landing")]
    public void ConductorDriverVerifiedRunsSemanticAcceptanceAfterAcceptanceAndLanding()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var order = new List<string>();

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ =>
            {
                order.Add("acceptance");
                return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
            },
            runAdvisorySemanticAcceptance: (_, _) => order.Add("semantic"),
            land: g =>
            {
                order.Add("land");
                return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed");
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Xunit.Assert.Equal(new[] { "acceptance", "land", "semantic" }, order);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_successful_landing_runs_semantic_then_post_landing_close_after_main_advances")]
    public void ConductorDriverVerifiedSuccessfulLandingRunsSemanticThenPostLandingCloseAfterMainAdvances()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var order = new List<string>();

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            runAdvisorySemanticAcceptance: (_, _) => order.Add("semantic"),
            land: g =>
            {
                order.Add("land");
                return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed");
            },
            afterSuccessfulLanding: (_, result) =>
            {
                Assert.True(result.MainAdvanced);
                order.Add("close");
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Xunit.Assert.Equal(new[] { "land", "semantic", "close" }, order);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_main_advance_schedules_relaunch_before_fallible_post_landing_actions")]
    public void ConductorDriverMainAdvanceSchedulesRelaunchBeforeFalliblePostLandingActions()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        ConductorLandingReceipt? receipt = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            runAdvisorySemanticAcceptance: (_, _) =>
                throw new InvalidOperationException("post-merge advisory failed"),
            land: g =>
                new LandingResult(
                    g.Id.Value,
                    g.Id.Value[..8],
                    new LandingDecision.Promote(),
                    "integration",
                    true,
                    "Landed"));
        driver.SuccessfulLandingSink = landed => receipt = landed;

        var error = Assert.Throws<InvalidOperationException>(
            () => driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative));

        Assert.Equal("post-merge advisory failed", error.Message);
        Assert.NotNull(receipt);
        Assert.Equal(goal.Id.Value, receipt!.GoalId);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_ownership_hold_escalates_and_skips_semantic_receipt")]
    public void ConductorDriverVerifiedOwnershipHoldEscalatesAndSkipsSemanticReceipt()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var semanticCalled = false;
        var landCalled = false;

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            runAdvisorySemanticAcceptance: (_, _) => { semanticCalled = true; },
            land: g =>
            {
                landCalled = true;
                return new LandingResult(
                    g.Id.Value,
                    g.Id.Value[..8],
                    new LandingDecision.Escalate("ownership-denylist hold: task touched protected path"),
                    "integration",
                    false,
                    "Held");
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Assert.Equal(GoalLifecycleState.Verified, escalated.State);
        Assert.True(LandingExecutor.IsOwnershipHoldEscalation(escalated.Reason));
        Assert.False(semanticCalled);
        Assert.True(landCalled);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_landing_writes_semantic_receipt_and_closes_backlog_item")]
    public async Task ConductorDriverVerifiedLandingWritesSemanticReceiptAndClosesBacklogItem()
    {
        var root = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(root, "src"));
        RunGit(root, "init");
        RunGit(root, "checkout", "-b", "main");
        RunGit(root, "config", "user.email", "test@example.com");
        RunGit(root, "config", "user.name", "Test User");
        File.WriteAllText(Path.Combine(root, "src", "A.cs"), "class A {}\n");
        RunGit(root, "add", ".");
        RunGit(root, "commit", "-m", "initial");
        RunGit(root, "checkout", "-b", "goal/test");
        File.WriteAllText(Path.Combine(root, "src", "A.cs"), "class A { string Done() => \"done\"; }\n");
        RunGit(root, "add", ".");
        RunGit(root, "commit", "-m", "goal change");
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog(
        [
            new ModelFunctionBinding(ModelFunctionPurposes.AcceptanceJudge, ModelLane.CheapApi,
                new ModelProfile("Fake", "judge", ModelCapability.Text, SubscriptionMode.ApiKey))
        ]));
        var provider = new CountingModelProvider("Fake",
            """{"criteria_met": true, "confidence": "high", "reasons": ["implemented"], "unmet_criteria": []}""");
        var providers = new InMemoryModelProviderRegistry([provider]);
        var backlogStore = new BacklogStore(workspace.BacklogStorePath);
        var item = await backlogStore.AddAsync("Landing target");
        var (kernel, goal) = SimpleGoal("Implement required behavior");
        kernel.SetGoalSourceBacklogItemId(goal.Id, item.Id);
        PassVerification(kernel, goal, goal.Tasks.Single());

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            runAdvisorySemanticAcceptance: (g, summary) => GoalLandingPostActions.RunAdvisorySemanticAcceptance(
                g,
                workspace,
                providers,
                WorkerProfileCatalog.Default(),
                root,
                null),
            land: g => new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed"),
            afterSuccessfulLanding: (g, result) =>
            {
                Assert.True(result.MainAdvanced);
                GoalLandingPostActions.AutoCloseSourceBacklogItem(g, workspace.BacklogStorePath);
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.True(provider.Calls > 0);
        var receipt = File.ReadLines(workspace.SemanticAcceptanceLogPath).Single();
        Assert.True(receipt.Contains(goal.Id.Value, StringComparison.Ordinal));
        var fetched = await backlogStore.GetByExactIdAsync(item.Id);
        Assert.NotNull(fetched);
        Assert.Equal(BacklogItemStatus.Done, fetched!.Status);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_does_not_close_backlog_when_landing_does_not_advance_main")]
    public void ConductorDriverVerifiedDoesNotCloseBacklogWhenLandingDoesNotAdvanceMain()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var closeCalled = false;

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            land: g => new LandingResult(g.Id.Value, g.Id.Value[..8],
                new LandingDecision.Promote(), "integration", false, "No main advance"),
            afterSuccessfulLanding: (_, _) => { closeCalled = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.False(closeCalled);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_zero_criterion_retry_budget_escalates_without_retry")]
    public void ConductorDriverVerifiedZeroCriterionRetryBudgetEscalatesWithoutRetry()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var retryCalled = false;
        string? escalationReason = null;
        var policy = ConductorAutonomyPolicy.Conservative with { MaxCriterionRetries = 0 };
        var unmet = new AcceptanceCheckResult(
            "command-exit dotnet test",
            false,
            1,
            "failed",
            ResultSummary: "focused command failed");

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptanceSummary: _ => new AcceptanceVerificationSummary(true, [unmet]),
            retryTask: (goalId, taskId, message) =>
            {
                retryCalled = true;
                return kernel.RetryTask(goalId, taskId, message);
            },
            writeEscalation: (_, _, reason) => { escalationReason = reason; });

        var result = driver.AdvanceOnce(goal, policy);

        Assert.False(retryCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
        Assert.Contains("Acceptance criteria unmet after 0 retries", escalationReason!, StringComparison.Ordinal);
        Assert.Contains("focused command failed", escalationReason!, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_security_risk_delegates_to_landing_engine")]
    public void ConductorDriverVerifiedSecurityRiskDelegatesToLandingEngine()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var escalated = false;
        var landCalled = false;

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptance: _ => true,
            classifyRisk: _ => ChangeRiskTier.Security,
            land: g =>
            {
                landCalled = true;
                return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed");
            },
            writeEscalation: (_, _, _) => { escalated = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(landCalled);
        Assert.False(escalated);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_landing_engine_escalation_escalates")]
    public void ConductorDriverVerifiedLandingEngineEscalationEscalates()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var escalated = false;

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptance: _ => true,
            classifyRisk: _ => ChangeRiskTier.DocsOnly,
            land: g => new LandingResult(g.Id.Value, g.Id.Value[..8],
                new LandingDecision.Escalate("Conflict detected"), "integration", false, "Conflict"),
            writeEscalation: (_, _, _) => { escalated = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(escalated);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_clean_DocsOnly_risk_lands")]
    public void ConductorDriverVerifiedCleanDocsOnlyRiskLands()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var landCalled = false;

        // Conservative: threshold DocsOnly, DocsOnly risk → Auto
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptance: _ => true,
            classifyRisk: _ => ChangeRiskTier.DocsOnly,
            land: g => { landCalled = true; return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed"); });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(landCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Equal(GoalLifecycleState.Verified, ((ConductorAdvanceOutcome.Executed)result.Outcome).FromState);
    }

    [Xunit.Theory(DisplayName = "Post-landing canary sink failure stays non-blocking and cannot skip successful-landing callbacks")]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void PostLandingCanaryFailureCannotSkipSuccessfulLandingCallbacks(bool breakSqliteStore)
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var root = CreateTempDirectory();
        var dbPath = Path.Combine(root, "run-events.db");
        if (breakSqliteStore)
        {
            Directory.CreateDirectory(dbPath);
        }

        var events = new PostLandingCanaryEventStore(
            new SqliteRunEventStore(dbPath, ensureSchema: !breakSqliteStore),
            dbPath);
        var circuit = new AcceptanceEngineCircuitBreaker(events);
        var coordinator = new PostLandingCanaryCoordinator(
            new PostLandingCanaryConfiguration(Enabled: true, TimeoutSeconds: 10, AdditionalEnginePathPrefixes: []),
            new PostLandingCanaryRunner(
                root,
                runOverride: (_, _) => Task.FromResult(PostLandingCanaryOutcome.Passed(1, "unused"))),
            events,
            circuit,
            progress: _ => { });
        var afterSuccessfulLandingCalled = false;
        var landingSha = breakSqliteStore ? "sha-broken-sqlite" : null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptance: _ => true,
            classifyRisk: _ => ChangeRiskTier.DocsOnly,
            land: g => new LandingResult(
                g.Id.Value,
                g.Id.Value[..8],
                new LandingDecision.Promote(),
                "integration",
                true,
                "Landed",
                landingSha,
                ["src/Mcg.AgentOrchestrator.App/Orchestration/PostLandingCanaryCoordinator.cs"]),
            afterSuccessfulLanding: (_, _) => afterSuccessfulLandingCalled = true);
        driver.SuccessfulLandingSink = receipt => coordinator.HandleLanding(receipt);

        try
        {
            var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

            Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
            Assert.True(afterSuccessfulLandingCalled);
            Assert.Equal(
                breakSqliteStore ? AcceptanceEngineHealth.Unavailable : AcceptanceEngineHealth.Healthy,
                circuit.Read().Health);
        }
        finally
        {
            PostLandingCanaryEmergencyCircuit.Clear(dbPath);
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Xunit.Fact(DisplayName = "ConductorDriver rereads circuit before parallel completion can invoke land")]
    public void ConductorDriverHoldsAtLandingMutationBoundary()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var landCalled = false;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptance: _ => true,
            classifyRisk: _ => ChangeRiskTier.DocsOnly,
            land: g =>
            {
                landCalled = true;
                return new LandingResult(
                    g.Id.Value,
                    g.Id.Value[..8],
                    new LandingDecision.Promote(),
                    "integration",
                    true,
                    "Landed");
            });
        driver.LandingMutationBlocker = () => "acceptance engine circuit is Pending";

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.False(landCalled);
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Equal(GoalLifecycleState.Verified, held.State);
        Assert.Contains("mutation boundary", held.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver treats a circuit race inside landing as a retryable hold")]
    public void ConductorDriverHoldsWhenCircuitOpensInsideLanding()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var escalationWritten = false;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptance: _ => true,
            classifyRisk: _ => ChangeRiskTier.DocsOnly,
            land: g => new LandingResult(
                g.Id.Value,
                g.Id.Value[..8],
                new LandingDecision.Escalate(
                    "landing mutation blocked: acceptance engine circuit is Pending"),
                "integration",
                false,
                "Landing held before merge"),
            writeEscalation: (_, _, _) => escalationWritten = true);
        driver.LandingMutationBlocker = () => null;

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.False(escalationWritten);
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Equal(GoalLifecycleState.Verified, held.State);
        Assert.Contains("circuit is Pending", held.Reason, StringComparison.Ordinal);
    }

    // ── Merged state ──────────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_Merged_records_and_returns_Executed")]
    public void ConductorDriverMergedRecordsAndReturnsExecuted()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var recorded = false;

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(IsMerged: true),
            record: _ => { recorded = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(recorded);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Equal(GoalLifecycleState.Merged, ((ConductorAdvanceOutcome.Executed)result.Outcome).FromState);
    }

    // ── Recorded state ────────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_Recorded_cleans_up_and_returns_Executed")]
    public void ConductorDriverRecordedCleansUpAndReturnsExecuted()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var cleanedUp = false;

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(IsMerged: true, IsRecorded: true),
            cleanup: _ =>
            {
                cleanedUp = true;
                return new GoalWorktreeRemoveResult("Workspace cleaned up.", null, [], null);
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(cleanedUp);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Equal(GoalLifecycleState.Recorded, ((ConductorAdvanceOutcome.Executed)result.Outcome).FromState);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Recorded_incomplete_cleanup_completes_with_deferred_diagnostics")]
    public void ConductorDriverRecordedIncompleteCleanupCompletesWithDeferredDiagnostics()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var cleanupCalls = 0;

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(IsMerged: true, IsRecorded: true),
            cleanup: _ =>
            {
                cleanupCalls++;
                return new GoalWorktreeRemoveResult(
                    "Removed workspace, but leftover directory cleanup is incomplete.",
                    @"C:\repo\.orchestrator-worktrees\abc12345",
                    [new WorktreeLockHolder(1234, "dotnet", "dotnet test")],
                    "conduct abc12345 --loop",
                    CleanupBackoff: new GoalWorktreeCleanupBackoff(
                        "remove:cleanup-budget-exhausted",
                        DateTimeOffset.Parse("2026-07-02T05:01:00Z"),
                        TimeSpan.FromMinutes(1)));
            },
            completeGoal: g => kernel.CompleteGoal(g.Id, "test completed after deferred cleanup."));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        var executed = Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.Equal(GoalLifecycleState.Recorded, executed.FromState);
        Assert.Equal(1, cleanupCalls);
        Assert.True(executed.Description.Contains("leftover", StringComparison.Ordinal), executed.Description);
        Assert.Equal(GoalStatus.Completed, kernel.GetGoal(goal.Id).Status);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Recorded_cleanup_failure_completes_without_throwing")]
    public void ConductorDriverRecordedCleanupFailureCompletesWithoutThrowing()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(IsMerged: true, IsRecorded: true),
            cleanup: _ => throw new InvalidOperationException("git worktree remove refused dirty workspace"),
            completeGoal: g => kernel.CompleteGoal(g.Id, "test completed after deferred cleanup."));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        var executed = Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.Equal(GoalLifecycleState.Recorded, executed.FromState);
        Assert.Contains("Workspace cleanup deferred after removal failure", executed.Description, StringComparison.Ordinal);
        Assert.Contains("git worktree remove refused dirty workspace", executed.Description, StringComparison.Ordinal);
        Assert.Equal(GoalStatus.Completed, kernel.GetGoal(goal.Id).Status);
    }

    // ── CleanedUp state ───────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_CleanedUp_returns_Done")]
    public void ConductorDriverCleanedUpReturnsDone()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(IsMerged: true, IsRecorded: true, IsCleanedUp: true));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(result.Outcome is ConductorAdvanceOutcome.Done);
        Assert.Equal(GoalLifecycleState.CleanedUp, ((ConductorAdvanceOutcome.Done)result.Outcome).State);
    }

    // ── Error states ──────────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_Failed_task_escalates_regardless_of_policy")]
    public void ConductorDriverFailedTaskEscalatesRegardlessOfPolicy()
    {
        var (kernel, goal) = SimpleGoal();
        FailVerification(kernel, goal, goal.Tasks.Single());

        var driver = MakeDriver(getFacts: _ => GoalLifecycleFacts.None);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Failed_task_escalation_surfaces_latest_TaskFailed_reason")]
    public void ConductorDriverFailedTaskEscalationSurfacesLatestTaskFailedReason()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        kernel.ReportTaskProgress(
            goal.Id,
            task.Id,
            WorkTaskStatus.Failed,
            "Reviewer WORKER_RESULT criteria attestation invalid: missing criterion_index 0.");
        var driver = MakeDriver(getFacts: _ => GoalLifecycleFacts.None);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Assert.Contains("TaskFailed: Reviewer WORKER_RESULT criteria attestation invalid", escalated.Reason, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_cancelled_goal_does_not_surface_task_failure_as_cancellation_reason")]
    public void ConductorDriverCancelledGoalDoesNotSurfaceTaskFailureAsCancellationReason()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "stale task failure");
        kernel.CancelGoal(goal.Id, "Operator cancelled the goal.");
        var driver = MakeDriver(getFacts: _ => GoalLifecycleFacts.None);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Assert.DoesNotContain("TaskFailed:", escalated.Reason, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_failed_goal_does_not_surface_failure_from_task_that_is_now_completed")]
    public void ConductorDriverFailedGoalDoesNotSurfaceFailureFromTaskThatIsNowCompleted()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        kernel.EscalateTaskFailure(goal.Id, task.Id, "stale task failure");
        kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Completed, "task recovered");
        var driver = MakeDriver(getFacts: _ => GoalLifecycleFacts.None);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Assert.DoesNotContain("TaskFailed:", escalated.Reason, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_auto_review_retry_convergence_brief_deduplicates_multi_round_findings")]
    public void ConductorDriverAutoReviewRetryConvergenceBriefDeduplicatesMultiRoundFindings()
    {
        var brief = AutoReviewRetryConvergenceBriefBuilder.BuildConvergenceBrief(
            3,
            AgentRole.Reviewer,
            new TaskId("reviewer-task-0001"),
            "verdict=needs-work",
            AgentRole.Developer,
            "C:\\tmp\\reviewer.out.log",
            [
                "ConductorDriverTests still expects the raw findings passthrough.",
                "Tester receipt omits ProgressiveReviewGlanceTests.",
                "  ConductorDriverTests   still expects the raw findings passthrough.  ",
                "Reviewer still needs InquiryDispatcherTests coverage."
            ],
            [
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTests.cs",
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProgressiveReviewGlanceTests.cs",
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/InquiryDispatcherTests.cs"
            ]);

        Assert.Contains("auto-review-retry round 3 convergence brief", brief);
        Assert.Contains("Existing implementation shape is accepted", brief);
        Assert.Contains("Do NOT rewrite", brief);
        Assert.Contains("Deduplicated residual blockers", brief);
        Assert.Equal(1, CountOccurrences(brief, "- ConductorDriverTests still expects the raw findings passthrough."));
        Assert.Contains("- Tester receipt omits ProgressiveReviewGlanceTests.", brief);
        Assert.Contains("- Reviewer still needs InquiryDispatcherTests coverage.", brief);
        Assert.Contains("Rerun these focused test classes at your final commit and quote receipts:", brief);
        Assert.Contains("ConductorDriverTests", brief);
        Assert.Contains("ProgressiveReviewGlanceTests", brief);
        Assert.Contains("InquiryDispatcherTests", brief);
        Assert.Contains("C:\\tmp\\reviewer.out.log", brief);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_auto_review_retry_convergence_brief_keeps_single_round_findings")]
    public void ConductorDriverAutoReviewRetryConvergenceBriefKeepsSingleRoundFindings()
    {
        var blocker = "Developer left tester receipt parsing unwired.";
        var brief = AutoReviewRetryConvergenceBriefBuilder.BuildConvergenceBrief(
            1,
            AgentRole.Tester,
            new TaskId("tester-task-0001"),
            "WORKER_RESULT blocker",
            AgentRole.Developer,
            "C:\\tmp\\tester.out.log",
            [blocker],
            []);

        Assert.Contains("auto-review-retry round 1 convergence brief", brief);
        Assert.Contains("Existing implementation shape is accepted", brief);
        Assert.Equal(1, CountOccurrences(brief, $"- {blocker}"));
        Assert.Contains("Rerun the focused test classes covering your changed files at your final commit and quote receipts.", brief);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_auto_review_retry_convergence_brief_keeps_all_unique_findings")]
    public void ConductorDriverAutoReviewRetryConvergenceBriefKeepsAllUniqueFindings()
    {
        var brief = AutoReviewRetryConvergenceBriefBuilder.BuildConvergenceBrief(
            2,
            AgentRole.Reviewer,
            new TaskId("reviewer-task-0001"),
            "verdict=needs-work",
            AgentRole.Developer,
            "reviewer output",
            [
                "First blocker remains open.",
                "Second blocker remains open.",
                "Third blocker remains open."
            ],
            ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ConductorDriverTests.cs"]);

        Assert.Equal(1, CountOccurrences(brief, "- First blocker remains open."));
        Assert.Equal(1, CountOccurrences(brief, "- Second blocker remains open."));
        Assert.Equal(1, CountOccurrences(brief, "- Third blocker remains open."));
        Assert.Contains("ConductorDriverTests", brief);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_reviewer_retry_convergence_brief_uses_latest_stable_finding_state")]
    public void ConductorDriverReviewerRetryConvergenceBriefUsesLatestStableFindingState()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(t => t.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        FailReviewerNeedsWork(kernel, goal, reviewer, "Shared blocker stays open.");
        kernel.RetryTask(goal.Id, developer.Id, "auto-review-retry round 1 convergence brief: prior retry");
        PassVerification(kernel, goal, developer);
        PassVerification(kernel, goal, tester);
        FailReviewerNeedsWork(kernel, goal, reviewer, "Shared blocker remains open after focused recheck.");
        string? retryMessage = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (gid, tid, msg, roundKind) =>
            {
                retryMessage = msg;
                return kernel.RetryTask(gid, tid, msg, retryRoundKind: roundKind);
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Contains("auto-review-retry round 2 convergence brief", retryMessage);
        Assert.Contains("## RESIDUAL_OPEN_ACTION_ITEMS", retryMessage);
        Assert.Equal(1, CountOccurrences(retryMessage!, "- stable_id: finding-1"));
        Assert.Contains("Shared blocker remains open after focused recheck.", retryMessage);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_reviewer_needs_work_auto_retries_developer_with_convergence_brief")]
    public void ConductorDriverReviewerNeedsWorkAutoRetriesDeveloperWithConvergenceBrief()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(t => t.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var blocker = "Developer omitted retry receipt injection in TaskBriefs.";
        FailReviewerNeedsWork(kernel, goal, reviewer, blocker);
        var dispatched = false;
        var escalated = false;
        string? retryMessage = null;
        RetryRoundKind? retryRoundKind = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => { dispatched = true; return DispatchStartOutcome.Started(); },
            retryTaskWithRoundKind: (gid, tid, msg, roundKind) =>
            {
                retryMessage = msg;
                retryRoundKind = roundKind;
                return kernel.RetryTask(gid, tid, msg, retryRoundKind: roundKind);
            },
            writeEscalation: (_, _, _) => { escalated = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(dispatched);
        Assert.False(escalated);
        Assert.Equal(WorkTaskStatus.Assigned, developer.Status);
        Assert.Equal(WorkTaskStatus.Assigned, tester.Status);
        Assert.Equal(WorkTaskStatus.Assigned, reviewer.Status);
        Assert.Contains("auto-review-retry round 1 convergence brief", retryMessage);
        Assert.Contains("Existing implementation shape is accepted", retryMessage);
        Assert.DoesNotContain("retry upstream Developer task with findings:", retryMessage);
        Assert.Contains(blocker, retryMessage);
        Assert.Contains("Rerun", retryMessage);
        Assert.Contains("C:\\tmp\\reviewer.out.log", retryMessage);
        Assert.Null(retryRoundKind);
        Assert.Null(developer.PendingRetryRoundKind);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == developer.Id &&
            evt.Kind == ProgressKind.TaskRetried &&
            evt.Message.Contains("auto-review-retry", StringComparison.OrdinalIgnoreCase));
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_reviewer_identity_violation_salvages_needs_work_for_developer")]
    public void ConductorDriverReviewerIdentityViolationSalvagesNeedsWorkForDeveloper()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(t => t.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var priorLocation = new ReviewFindingLocation("src/A.cs", "A.Run", "guard");
        DispatchTask(kernel, goal, reviewer, "review-1");
        kernel.RecordDispatchBaseCommit(goal.Id, reviewer.Id, "prior-commit");
        FailStructuredReviewerRound(
            kernel,
            goal,
            reviewer,
            "review-1",
            [new ReviewFinding("F-CARRIED", ReviewFindingState.Open, priorLocation, "Carried blocker remains.")]);

        kernel.RetryTask(goal.Id, reviewer.Id, "recheck");
        DispatchTask(kernel, goal, reviewer, "review-2");
        kernel.RecordDispatchBaseCommit(goal.Id, reviewer.Id, "current-commit");
        FailStructuredReviewerRound(
            kernel,
            goal,
            reviewer,
            "review-2",
            [
                new ReviewFinding(
                    "F-CARRIED",
                    ReviewFindingState.Open,
                    new ReviewFindingLocation("src/Moved.cs", "Moved.Run", "guard"),
                    "Carried blocker remains."),
                new ReviewFinding(
                    "F-NEW",
                    ReviewFindingState.Open,
                    new ReviewFindingLocation("src/New.cs", "New.Run"),
                    "New blocker also remains.")
            ]);

        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
        Assert.Equal(
            ReviewFindingConvergence.IdentityMovedViolationCode,
            reviewer.LastVerification!.ReviewFindingContractViolation?.Code);
        Assert.Equal(priorLocation, reviewer.LastVerification.MergedReviewFindings!.Single(item => item.StableId == "F-CARRIED").Location);

        TaskId? retriedTaskId = null;
        string? retryMessage = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (gid, tid, message, roundKind) =>
            {
                retriedTaskId = tid;
                retryMessage = message;
                return kernel.RetryTask(gid, tid, message, retryRoundKind: roundKind);
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(developer.Id, retriedTaskId);
        Assert.Contains("F-CARRIED", retryMessage, StringComparison.Ordinal);
        Assert.Contains("F-NEW", retryMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("contract-repair", retryMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_structured_open_findings_retry_when_blockers_is_none")]
    public void ConductorDriverStructuredOpenFindingsRetryWhenBlockersIsNone()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(t => t.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        DispatchTask(kernel, goal, reviewer, "review");
        var stdout = string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "commands: review",
            "tests: pass - inspected evidence",
            "blockers: none",
            """findings: [{"stable_id":"F-OPEN","state":"open","location":{"file":"src/Test.cs","region":"Test.Run","hunk":"guard"},"description":"Developer must restore the guard."}]""",
            "touched_anchors: []",
            "verdict: needs-work",
            "END_WORKER_RESULT");
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review",
            "C:\\tmp",
            0,
            stdout,
            "",
            DateTimeOffset.UtcNow,
            StandardOutputPath: "C:\\tmp\\reviewer.out.log",
            WorkerResultPresent: true));
        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);

        string? retryMessage = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTask: (gid, tid, msg) =>
            {
                retryMessage = msg;
                return kernel.RetryTask(gid, tid, msg);
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(WorkTaskStatus.Assigned, developer.Status);
        Assert.Contains("- stable_id: F-OPEN", retryMessage);
        Assert.Contains("Developer must restore the guard.", retryMessage);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_unparseable_typed_evidence_finding_reports_typed_refusal")]
    public void ConductorDriverUnparseableTypedEvidenceFindingReportsTypedRefusal()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(t => t.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        DispatchTask(kernel, goal, reviewer, "review");
        var stdout = string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "commands: review",
            "tests: pass - inspected evidence",
            "blockers: focused receipt is missing",
            """findings: [{"stable_id":"F-TEST","state":"open","severity":"blocking","category":"test-evidence","location":{"file":"tests/Test.cs","region":"Test.Run"},"description":"Focused receipt is missing.","evidence_request":{"selections":[]}}]""",
            "touched_anchors: []",
            "verdict: needs-work",
            "END_WORKER_RESULT");
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review",
            "C:\\tmp",
            0,
            stdout,
            "",
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true));
        TaskId? retriedTask = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTask: (goalId, taskId, message) =>
            {
                retriedTask = taskId;
                return kernel.RetryTask(goalId, taskId, message);
            },
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(reviewer.Id, retriedTask);
        Assert.Contains(goal.Timeline, evt =>
            evt.Kind == ProgressKind.FindingEvidenceRequestRecorded &&
            evt.Message.Contains("role=Reviewer", StringComparison.Ordinal) &&
            evt.Message.Contains("reason=unparseable-selection", StringComparison.Ordinal));
        Assert.Equal(
            FindingEvidenceNotHonouredReason.UnparseableSelection,
            reviewer.VerificationHistory.Last().MergedReviewFindings?.Single().EvidenceOutcome?.Reason);
        var retryBrief = kernel.BuildTaskBrief(goal.Id, reviewer.Id).Content;
        Assert.Contains("verdict=not-honoured", retryBrief, StringComparison.Ordinal);
        Assert.Contains("reason=unparseable-selection", retryBrief, StringComparison.Ordinal);
        Assert.Contains("evidence_not_honoured: detail=", retryBrief, StringComparison.Ordinal);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_tester_worker_result_blocker_auto_retries_developer_with_convergence_brief")]
    public void ConductorDriverTesterWorkerResultBlockerAutoRetriesDeveloperWithConvergenceBrief()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Tester);
        PassVerification(kernel, goal, developer, hasCommittedChanges: true);

        var blocker = "Developer left the retry feedback path unwired.";
        FailTesterBlocker(kernel, goal, tester, blocker);
        var dispatched = false;
        var escalated = false;
        TaskId? retriedTaskId = null;
        string? retryMessage = null;
        RetryRoundKind? retryRoundKind = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => { dispatched = true; return DispatchStartOutcome.Started(); },
            retryTaskWithRoundKind: (gid, tid, msg, roundKind) =>
            {
                retriedTaskId = tid;
                retryMessage = msg;
                retryRoundKind = roundKind;
                return kernel.RetryTask(gid, tid, msg, retryRoundKind: roundKind);
            },
            writeEscalation: (_, _, _) => { escalated = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(dispatched);
        Assert.False(escalated);
        Assert.Equal(developer.Id, retriedTaskId);
        Assert.Equal(WorkTaskStatus.Assigned, developer.Status);
        Assert.Equal(WorkTaskStatus.Assigned, tester.Status);
        Assert.Contains("auto-review-retry round 1 convergence brief", retryMessage);
        Assert.Contains("Tester task", retryMessage);
        Assert.Contains("WORKER_RESULT blocker", retryMessage);
        Assert.Contains("Existing implementation shape is accepted", retryMessage);
        Assert.DoesNotContain("retry upstream Developer task with findings:", retryMessage);
        Assert.Contains(blocker, retryMessage);
        Assert.Contains("Rerun", retryMessage);
        Assert.Contains("C:\\tmp\\tester.out.log", retryMessage);
        Assert.Null(retryRoundKind);
        Assert.Null(developer.PendingRetryRoundKind);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == developer.Id &&
            evt.Kind == ProgressKind.TaskRetried &&
            evt.Message.Contains("auto-review-retry", StringComparison.OrdinalIgnoreCase));
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_tester_identity_violation_salvages_failure_for_developer")]
    public void ConductorDriverTesterIdentityViolationSalvagesFailureForDeveloper()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Tester);
        PassVerification(kernel, goal, developer, hasCommittedChanges: true);

        var priorLocation = new ReviewFindingLocation("tests/A.cs", "A.Tests", "guard");
        DispatchTask(kernel, goal, tester, "test-1");
        kernel.RecordDispatchBaseCommit(goal.Id, tester.Id, "prior-commit");
        kernel.RecordDispatchExecutionResult(goal.Id, tester.Id, new TaskVerificationRecord(
            "test-1",
            "C:\\tmp",
            0,
            ReviewerPassWithFinding(
                new ReviewFinding(
                    "T-1",
                    ReviewFindingState.Open,
                    priorLocation,
                    "Residual behavior test still fails.")),
            string.Empty,
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true));

        kernel.RetryTask(goal.Id, tester.Id, "recheck");
        DispatchTask(kernel, goal, tester, "test-2");
        kernel.RecordDispatchBaseCommit(goal.Id, tester.Id, "current-commit");
        var blocker = "Residual behavior test still fails.";
        var moved = new ReviewFinding(
            "T-1",
            ReviewFindingState.Open,
            new ReviewFindingLocation("tests/B.cs", "B.Tests", "guard"),
            blocker);
        var stdout = string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "commands: focused test",
            "tests: fail - one residual failure",
            "commit: none",
            $"blockers: {blocker}",
            $"findings: {JsonSerializer.Serialize(new[] { moved })}",
            "touched_anchors: []",
            "verdict: needs-work",
            "model_fit: fixture/model - adequate - focused test",
            "skills: none",
            "confidence: high",
            "END_WORKER_RESULT");
        kernel.RecordDispatchExecutionResult(goal.Id, tester.Id, new TaskVerificationRecord(
            "test-2",
            "C:\\tmp",
            1,
            stdout,
            string.Empty,
            DateTimeOffset.UtcNow,
            StandardOutputPath: "C:\\tmp\\tester.out.log",
            WorkerResultPresent: true));

        Assert.Equal(WorkTaskStatus.Failed, tester.Status);
        Assert.Equal(
            ReviewFindingConvergence.IdentityMovedViolationCode,
            tester.LastVerification!.ReviewFindingContractViolation?.Code);
        Assert.Equal(priorLocation, Assert.Single(tester.LastVerification.MergedReviewFindings!).Location);
        Assert.True(WorkerResultBlockers.TryGetTestsStatus(tester.LastVerification, out var testsStatus));
        Assert.Equal(WorkerResultBlockers.TestsStatus.Fail, testsStatus);
        Assert.True(WorkerResultBlockers.TryFindHardFailureBlocker(tester.LastVerification, out _));

        TaskId? retriedTaskId = null;
        string? retryMessage = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (gid, tid, message, roundKind) =>
            {
                retriedTaskId = tid;
                retryMessage = message;
                return kernel.RetryTask(gid, tid, message, retryRoundKind: roundKind);
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(
            developer.Id == retriedTaskId,
            $"Expected Developer retry but got {retriedTaskId?.Value ?? "none"}; outcome={result.Outcome}.");
        Assert.Contains(blocker, retryMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("contract-repair", retryMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_tester_worker_result_blocker_uses_shared_auto_review_retry_cap")]
    public void ConductorDriverTesterWorkerResultBlockerUsesSharedAutoReviewRetryCap()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Tester);
        for (var i = 1; i <= 6; i++)
        {
            kernel.RetryTask(goal.Id, developer.Id, $"auto-review-retry round {i}: prior verifying-role finding");
            PassVerification(kernel, goal, developer, hasCommittedChanges: true);
        }

        FailTesterBlocker(kernel, goal, tester, "Developer still misses the tester correctness blocker.");
        var retried = false;
        var dispatched = false;
        string? escalation = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => { dispatched = true; return DispatchStartOutcome.Started(); },
            retryTask: (gid, tid, msg) => { retried = true; return kernel.RetryTask(gid, tid, msg); },
            writeEscalation: (_, _, message) => { escalation = message; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.False(retried);
        Assert.False(dispatched);
        Assert.Contains("auto-review-retry stopped at review round 7/7", escalation);
        Assert.Contains("operator decision required", escalation);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_tester_empty_output_flake_auto_recovers_without_developer_retry")]
    public void ConductorDriverTesterEmptyOutputFlakeAutoRecoversWithoutDeveloperRetry()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Tester);
        PassVerification(kernel, goal, developer, hasCommittedChanges: true);
        DispatchTask(kernel, goal, tester, "test");
        kernel.RecordDispatchExecutionResult(goal.Id, tester.Id,
            new TaskVerificationRecord("test", "C:\\tmp", 1, "", "", DateTimeOffset.UtcNow));

        TaskId? retriedTaskId = null;
        var escalated = false;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (gid, tid, msg, roundKind) =>
            {
                retriedTaskId = tid;
                return kernel.RetryTask(gid, tid, msg, retryRoundKind: roundKind);
            },
            writeEscalation: (_, _, _) => { escalated = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.False(escalated);
        Assert.Equal(tester.Id, retriedTaskId);
        Assert.NotEqual(developer.Id, retriedTaskId);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Tester_inconclusive_retries_same_task_and_preserves_upstream_commit")]
    public void ConductorDriverTesterInconclusiveRetriesSameTaskAndPreservesUpstreamCommit()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks)
        {
            PassVerification(kernel, goal, task, hasCommittedChanges: task == developer);
        }

        kernel.RecordDispatchBaseCommit(goal.Id, developer.Id, "base1234");
        kernel.RecordDispatchResultCommit(goal.Id, developer.Id, "result5678");
        var developerVerification = developer.LastVerification;
        var developerDispatch = developer.LastDispatch;
        kernel.RetryTask(goal.Id, tester.Id, "simulate environmental re-verification");
        DispatchTask(kernel, goal, tester, "test-inconclusive");
        var stdout = string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "commands: dotnet test --no-build --filter Focused",
            "tests: inconclusive - command timed out; no TRX",
            "commit: none",
            "blockers: stale product blocker from prior round",
            "model_fit: fixture/model - adequate - tester retry",
            "skills: dotnet-windows-build-hygiene",
            "confidence: medium",
            "END_WORKER_RESULT");
        kernel.RecordDispatchExecutionResult(
            goal.Id,
            tester.Id,
            new TaskVerificationRecord(
                "test-inconclusive",
                "C:\\tmp",
                0,
                stdout,
                "",
                DateTimeOffset.UtcNow,
                WorkerResultPresent: true));

        TaskId? retriedTaskId = null;
        string? retryMessage = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (gid, tid, message, roundKind) =>
            {
                retriedTaskId = tid;
                retryMessage = message;
                return kernel.RetryTask(gid, tid, message, retryRoundKind: roundKind);
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(tester.Id, retriedTaskId);
        Assert.NotEqual(developer.Id, retriedTaskId);
        Assert.Contains("verification-inconclusive Tester task", retryMessage, StringComparison.Ordinal);
        Assert.Contains("schema conflict retained for audit", retryMessage, StringComparison.Ordinal);
        Assert.Equal(WorkTaskStatus.Assigned, tester.Status);
        Assert.Equal(WorkTaskStatus.Assigned, reviewer.Status);
        Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        Assert.Same(developerVerification, developer.LastVerification);
        Assert.Same(developerDispatch, developer.LastDispatch);
        Assert.Equal("result5678", developer.LastDispatch!.ResultCommit);
        Assert.Equal(2, tester.VerificationHistory.Count);
        Assert.DoesNotContain(goal.Timeline, evt =>
            evt.TaskId == developer.Id && evt.Kind == ProgressKind.TaskRetried);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Tester_inconclusive_budget_exhaustion_escalates_with_latest_receipt")]
    public void ConductorDriverTesterInconclusiveBudgetExhaustionEscalatesWithLatestReceipt()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Tester);
        foreach (var task in goal.Tasks.TakeWhile(task => task.RequiredRole != AgentRole.Tester))
        {
            PassVerification(kernel, goal, task, hasCommittedChanges: task == developer);
        }

        var policy = ConductorAutonomyPolicy.Permissive with
        {
            MaxEmptyOutputDispatchRetries = 1,
            MaxEmptyOutputAutoRecoverCycles = 1
        };
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (gid, tid, message, roundKind) =>
                kernel.RetryTask(gid, tid, message, retryRoundKind: roundKind));

        RecordInconclusiveTester(kernel, goal, tester, "round 1 process killed; no TRX");
        var result = driver.AdvanceOnce(goal, policy);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);

        RecordInconclusiveTester(kernel, goal, tester, "round 2 timed out; no results");
        string? escalationMessage = null;
        var escalationDriver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => throw new Xunit.Sdk.XunitException("Developer or Tester dispatch must not start after retry exhaustion."),
            retryTaskWithRoundKind: (_, _, _, _) => throw new Xunit.Sdk.XunitException("Retry budget is exhausted."),
            writeEscalation: (_, _, message) => escalationMessage = message);

        result = escalationDriver.AdvanceOnce(goal, policy);

        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
        Assert.Contains(tester.Id.Value[..8], escalationMessage, StringComparison.Ordinal);
        Assert.Contains("round 2 timed out; no results", escalationMessage, StringComparison.Ordinal);
        Assert.Equal(2, tester.VerificationHistory.Count);
        Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        Assert.DoesNotContain(goal.Timeline, evt =>
            evt.TaskId == developer.Id && evt.Kind == ProgressKind.TaskRetried);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_premise_invalid_planner_waits_for_operator_before_Developer_dispatch")]
    public void ConductorDriverPremiseInvalidPlannerWaitsForOperatorBeforeDeveloperDispatch()
    {
        var (kernel, goal) = SoftwareGoal();
        var planner = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Planner);
        var developer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
        DispatchTask(kernel, goal, planner, "plan");
        var stdout = string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "commands: inspect src/Api.cs",
            "tests: not-run - repository inspection only",
            "commit: none",
            "blockers: premise-invalid - required API does not exist; src/Api.cs proves replacement semantics",
            "model_fit: fixture/model - adequate - planner inspection",
            "skills: none",
            "confidence: high",
            "END_WORKER_RESULT");
        kernel.RecordDispatchExecutionResult(
            goal.Id,
            planner.Id,
            new TaskVerificationRecord(
                "plan",
                "C:\\tmp",
                0,
                stdout,
                "",
                DateTimeOffset.UtcNow,
                WorkerResultPresent: true));

        var dispatched = false;
        string? escalationMessage = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ =>
            {
                dispatched = true;
                return DispatchStartOutcome.Started();
            },
            writeEscalation: (_, _, message) => escalationMessage = message);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.False(dispatched);
        Assert.Null(developer.LastDispatch);
        Assert.Equal(WorkTaskStatus.WaitingForHuman, planner.Status);
        Assert.Single(kernel.GetPendingHumanInput(goal.Id));
        Assert.Contains("AwaitingHumanInput", escalationMessage, StringComparison.Ordinal);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_auto_review_retry_drops_superseded_findings_before_retry_feedback")]
    public void ConductorDriverAutoReviewRetryDropsSupersededFindingsBeforeRetryFeedback()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(t => t.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        kernel.RecordTaskNote(
            goal.Id,
            reviewer.Id,
            "CRITERIA CORRECTION: supersedes=\"full Infrastructure suite before review\"; correction=\"focused build-check evidence is sufficient\"");
        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            "missing full Infrastructure suite before review; Developer omitted retry receipt injection in TaskBriefs.");
        string? retryMessage = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (gid, tid, msg, roundKind) =>
            {
                retryMessage = msg;
                return kernel.RetryTask(gid, tid, msg, retryRoundKind: roundKind);
            },
            recordTaskNote: (gid, tid, message) => kernel.RecordTaskNote(gid, tid, message));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(WorkTaskStatus.Assigned, developer.Status);
        Assert.Contains("Developer omitted retry receipt injection in TaskBriefs", retryMessage);
        Assert.DoesNotContain("missing full Infrastructure suite before review", retryMessage);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == reviewer.Id &&
            evt.Kind == ProgressKind.TaskNote &&
            evt.Message.Contains("Suppressed auto-review-retry finding", StringComparison.Ordinal) &&
            evt.Message.Contains("missing full Infrastructure suite before review", StringComparison.Ordinal));
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_reviewer_evidence_request_runs_focused_evidence_and_retries_reviewer_only")]
    public void ConductorDriverReviewerEvidenceRequestRunsFocusedEvidenceAndRetriesReviewerOnly()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(t => t.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var request = "Infrastructure.Tests:ConductorDriverTests";
        FailReviewerNeedsWork(kernel, goal, reviewer, "missing focused conductor evidence", request);
        var focusedRuns = 0;
        var retriedTaskIds = new List<TaskId>();
        string? retryMessage = null;
        RetryRoundKind? retryRoundKind = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, actualRequest) =>
            {
                focusedRuns++;
                Assert.Equal(request, actualRequest);
                return new FocusedEvidenceRunResult(
                    actualRequest,
                    Accepted: true,
                    Passed: true,
                    Summary: "focused evidence passed: 1 check; receipts: C:\\tmp\\trx",
                    Checks:
                    [
                        new AcceptanceCheckResult(
                            "reviewer focused evidence: Infrastructure.Tests FullyQualifiedName~ConductorDriverTests",
                            true,
                            0,
                            null,
                            ArtifactsPath: "C:\\tmp\\trx")
                    ]);
            },
            retryTaskWithRoundKind: (gid, tid, msg, roundKind) =>
            {
                retriedTaskIds.Add(tid);
                retryMessage = msg;
                retryRoundKind = roundKind;
                return kernel.RetryTask(gid, tid, msg, retryRoundKind: roundKind);
            },
            recordFindingEvidenceRequest: (gid, tid, msg) => kernel.RecordFindingEvidenceRequest(gid, tid, msg),
            recordFindingEvidenceRun: (gid, tid, msg) => kernel.RecordFindingEvidenceRun(gid, tid, msg),
            recordFindingEvidenceOutcome: (gid, tid, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(gid, tid, stableId, outcome, receipt));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        Assert.Equal([reviewer.Id], retriedTaskIds);
        Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        Assert.Equal(WorkTaskStatus.Completed, tester.Status);
        Assert.Equal(WorkTaskStatus.Assigned, reviewer.Status);
        Assert.Contains("finding evidence-on-demand", retryMessage);
        var receipt = Assert.Single(reviewer.VerificationHistory.SelectMany(item => item.FindingEvidenceReceipts ?? []));
        Assert.Contains("C:\\tmp\\trx", receipt.Summary);
        var evidenceOutcome = Assert.Single(reviewer.VerificationHistory.Last().MergedReviewFindings!).EvidenceOutcome;
        Assert.Equal(FindingEvidenceOutcomeReason.Unknown, evidenceOutcome?.ResultReason);
        Assert.Equal(RetryRoundKind.Mechanical, retryRoundKind);
        Assert.Equal(RetryRoundKind.Mechanical, reviewer.PendingRetryRoundKind);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == reviewer.Id &&
            evt.Kind == ProgressKind.FindingEvidenceRequestRecorded &&
            evt.Message.Contains("reason=unknown", StringComparison.Ordinal));
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == reviewer.Id &&
            evt.Kind == ProgressKind.FindingEvidenceRunRecorded &&
            evt.Message.Contains("outcome=unknown", StringComparison.Ordinal));
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Theory]
    [Xunit.InlineData(FindingCategory.Correctness)]
    [Xunit.InlineData(FindingCategory.Unspecified)]
    public void StructuredRequestRunsDespiteUnrelatedBlocker(FindingCategory category)
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        const string evidenceBlocker = "Infrastructure.Tests ConductorDriverTests receipts are missing.";
        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            $"{evidenceBlocker}; defect remains",
            findings:
            [
                EvidenceFindingWithRequest(
                    evidenceBlocker,
                    category: FindingCategory.Correctness,
                    classes: ["ConductorDriverTests"]),
                new ReviewFinding(
                    "other-blocker",
                    ReviewFindingState.Open,
                    new ReviewFindingLocation("src/Test.cs", "Run"),
                    "defect remains",
                    FindingSeverity.Blocking,
                    category)
            ]);
        var focusedRuns = 0;
        TaskId? retriedTaskId = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                Assert.Equal("Infrastructure.Tests:ConductorDriverTests", request);
                return new FocusedEvidenceRunResult(request, true, true, "focused request passed", []);
            },
            retryTask: (goalId, taskId, message) =>
            {
                retriedTaskId = taskId;
                return kernel.RetryTask(goalId, taskId, message);
            },
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        Assert.Equal(reviewer.Id, retriedTaskId);
        Assert.Contains(goal.Timeline, evt =>
            evt.Kind == ProgressKind.FindingEvidenceRequestRecorded &&
            evt.Message.Contains("disposition=honoured", StringComparison.Ordinal) &&
            evt.Message.Contains("finding_id=missing-receipts", StringComparison.Ordinal));
        var findings = reviewer.VerificationHistory.Last().MergedReviewFindings!;
        Assert.True(findings.Single(finding => finding.StableId == "missing-receipts").EvidenceOutcome?.Honoured);
        Assert.Null(findings.Single(finding => finding.StableId == "other-blocker").EvidenceOutcome);
    }

    [Xunit.Fact]
    public void TesterStructuredRequestReceivesFindingBoundReceiptInRetryContext()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        PassVerification(kernel, goal, developer);
        DispatchTask(kernel, goal, tester, "test");
        var finding = EvidenceFindingWithRequest(
            "Conductor routing needs an executed receipt.",
            id: "tester-evidence",
            category: FindingCategory.Correctness,
            project: "Mcg.AgentOrchestrator.Infrastructure.Tests",
            classes: ["ConductorDriverTests"]);
        var output = string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "commands: test",
            "tests: fail - receipt missing",
            "blockers: exact-blocker - receipt missing",
            $"findings: {JsonSerializer.Serialize(new[] { finding })}",
            "touched_anchors: []",
            "model_fit: test/test - adequate - fixture - fixture",
            "skills: none",
            "confidence: high",
            "END_WORKER_RESULT");
        kernel.RecordDispatchExecutionResult(
            goal.Id,
            tester.Id,
            new TaskVerificationRecord(
                "test", "C:\\tmp", 1, output, "", DateTimeOffset.UtcNow, WorkerResultPresent: true));

        var focusedRuns = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult(request, true, true, "tester requested evidence passed", []);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceRun: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRun(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        Assert.Equal(WorkTaskStatus.Assigned, tester.Status);
        var testerBrief = kernel.BuildTaskBrief(goal.Id, tester.Id).Content;
        Assert.Contains("evidence_receipt:", testerBrief, StringComparison.Ordinal);
        Assert.Contains("tester requested evidence passed", testerBrief, StringComparison.Ordinal);
        var nonRequesterBrief = kernel.BuildTaskBrief(goal.Id, developer.Id).Content;
        Assert.Contains("evidence_index:", nonRequesterBrief, StringComparison.Ordinal);
        Assert.DoesNotContain("evidence_receipt:", nonRequesterBrief, StringComparison.Ordinal);
        Assert.DoesNotContain("tester requested evidence passed", nonRequesterBrief, StringComparison.Ordinal);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == tester.Id &&
            evt.Kind == ProgressKind.FindingEvidenceRequestRecorded &&
            evt.Message.Contains("role=Tester", StringComparison.Ordinal) &&
            evt.Message.Contains("finding_id=tester-evidence", StringComparison.Ordinal));
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == tester.Id &&
            evt.Kind == ProgressKind.FindingEvidenceRunRecorded &&
            evt.Message.Contains("role=Tester", StringComparison.Ordinal) &&
            evt.Message.Contains("finding_id=tester-evidence", StringComparison.Ordinal));
        var recorded = tester.VerificationHistory.Last().MergedReviewFindings!.Single(item => item.StableId == "tester-evidence");
        Assert.True(recorded.EvidenceOutcome?.Honoured);
    }

    [Xunit.Theory]
    [Xunit.InlineData("Mcg.AgentOrchestrator.Infrastructure.Tests", "Infrastructure.Tests")]
    [Xunit.InlineData("Mcg.AgentOrchestrator.Core.Tests", "Core.Tests")]
    [Xunit.InlineData("Infrastructure.Tests", "Infrastructure.Tests")]
    [Xunit.InlineData("Core.Tests", "Core.Tests")]
    [Xunit.InlineData("Infrastructure", "Infrastructure.Tests")]
    [Xunit.InlineData("Core", "Core.Tests")]
    [Xunit.InlineData("C:\\repo\\tests\\Mcg.AgentOrchestrator.Infrastructure.Tests\\Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", "Infrastructure.Tests")]
    public void ReviewerStructuredRequestAcceptsEveryFocusedEvidenceProjectForm(
        string project,
        string canonicalProject)
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = EvidenceFindingWithRequest(
            "The project form must reach focused evidence execution.",
            id: "project-form",
            project: project,
            classes: ["ConductorDriverTests"]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence required", findings: [finding]);
        var focusedRuns = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                Assert.Equal($"{canonicalProject}:ConductorDriverTests", request);
                return new FocusedEvidenceRunResult(request, true, true, "project form accepted", []);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        var outcome = reviewer.VerificationHistory.Last().MergedReviewFindings!
            .Single(item => item.StableId == "project-form")
            .EvidenceOutcome;
        Assert.True(outcome?.Honoured);
        Assert.Null(outcome?.Reason);
    }

    [Xunit.Fact]
    public void ReviewerStructuredRequestRoundTripsSystemEmittedFullyQualifiedName()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = EvidenceFindingWithRequest(
            "The system-emitted selection must round-trip unchanged.",
            id: "system-round-trip",
            classes: ["FullyQualifiedName~ConductorDriverTests"]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence required", findings: [finding]);
        string? observedRequest = null;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                observedRequest = request;
                return new FocusedEvidenceRunResult(request, true, true, "round-trip accepted", []);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(
            "Infrastructure.Tests:FullyQualifiedName~ConductorDriverTests",
            observedRequest);
        Assert.Equal(
            "FullyQualifiedName~ConductorDriverTests",
            reviewer.VerificationHistory.Last().FindingEvidenceReceipts!.Single()
                .Request.Selections.Single().TestClass);
    }

    [Xunit.Fact]
    public void ReviewerStructuredRequestPreservesExactFilterTokenWhitespace()
    {
        const string originalToken = "  FullyQualifiedName~MissingSelectionTests.MissingMethod  ";
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = EvidenceFindingWithRequest(
            "The exact structured token must reach typed executor rejection.",
            id: "exact-structured-token",
            classes: [originalToken]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence required", findings: [finding]);
        string? observedRequest = null;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                observedRequest = request;
                return new FocusedEvidenceRunResult(
                    request,
                    Accepted: false,
                    Passed: false,
                    Summary: "selection rejected",
                    Checks: [],
                    Rejection: new FocusedEvidenceRejection(
                        FocusedEvidenceRejectionCode.UnresolvableSelection,
                        originalToken,
                        "selection rejected"));
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal("Infrastructure.Tests:" + originalToken, observedRequest);
        var outcome = reviewer.VerificationHistory.Last().MergedReviewFindings!
            .Single(item => item.StableId == "exact-structured-token")
            .EvidenceOutcome;
        Assert.Equal(FindingEvidenceNotHonouredReason.UnparseableSelection, outcome?.Reason);
        Assert.Contains(originalToken, outcome?.Detail, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void ReviewerStructuredRequestKeepsMultipleFqnSelectionsExecutorValid()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = EvidenceFindingWithRequest(
            "Each structured selection must remain a complete executor item.",
            id: "multiple-fqn-selections",
            classes:
            [
                "FullyQualifiedName~GoalAcceptanceVerifierTests",
                "FullyQualifiedName~ConductorDriverTests"
            ]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence required", findings: [finding]);
        string? observedRequest = null;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                observedRequest = request;
                return new FocusedEvidenceRunResult(request, true, true, "selections accepted", []);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(
            "Infrastructure.Tests:FullyQualifiedName~ConductorDriverTests; " +
            "Infrastructure.Tests:FullyQualifiedName~GoalAcceptanceVerifierTests",
            observedRequest);
    }

    [Xunit.Fact]
    public void ReviewerStructuredRequestRoundTripsSystemEmittedCompositeFilter()
    {
        var emitted = ConductorDriver.BuildPreReviewEvidenceContext(
            "abc1234",
            ["src/Mcg.AgentOrchestrator.App/Dashboard/Rendering/DashboardRenderer.OperatorShell.cs"]);
        var emittedRequest = Assert.IsType<string>(emitted.FocusedRequest);
        var separator = emittedRequest.IndexOf(':', StringComparison.Ordinal);
        var project = emittedRequest[..separator];
        var originalFilter = emittedRequest[(separator + 1)..].TrimStart();
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = EvidenceFindingWithRequest(
            "The planner-emitted composite filter must round-trip unchanged.",
            id: "system-composite-round-trip",
            project: project,
            classes: [originalFilter]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence required", findings: [finding]);
        string? observedRequest = null;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                observedRequest = request;
                return new FocusedEvidenceRunResult(request, true, true, "composite accepted", []);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Contains("|", originalFilter, StringComparison.Ordinal);
        Assert.Contains("Category!=HostIntegration", originalFilter, StringComparison.Ordinal);
        Assert.Equal(project + ":" + originalFilter, observedRequest);
    }

    [Xunit.Fact]
    public void ReviewerStructuredRequestPreservesUnpaddedExactFilterToken()
    {
        const string originalToken = "FullyQualifiedName~ConductorDriverTests.MissingMethod";
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = EvidenceFindingWithRequest(
            "An unresolvable token must retain its exact spelling.",
            id: "unpadded-exact-token",
            classes: [originalToken]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence required", findings: [finding]);
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                Assert.Equal("Infrastructure.Tests:" + originalToken, request);
                return new FocusedEvidenceRunResult(
                    request,
                    Accepted: false,
                    Passed: false,
                    Summary: "selection rejected",
                    Checks: [],
                    Rejection: new FocusedEvidenceRejection(
                        FocusedEvidenceRejectionCode.UnresolvableSelection,
                        originalToken,
                        "selection rejected"));
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        var outcome = reviewer.VerificationHistory.Last().MergedReviewFindings!
            .Single(item => item.StableId == "unpadded-exact-token")
            .EvidenceOutcome;
        Assert.Equal(FindingEvidenceNotHonouredReason.UnparseableSelection, outcome?.Reason);
        Assert.Contains(originalToken, outcome?.Detail, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void SourceDiscoveryFailureUsesTypedSelectionApparatusDisposition()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = EvidenceFindingWithRequest(
            "Unreadable source is an apparatus failure, not an invalid selector.",
            id: "source-discovery-apparatus",
            classes: ["FullyQualifiedName~ConductorDriverTests.MissingMethod"]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence required", findings: [finding]);
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) => new FocusedEvidenceRunResult(
                request,
                Accepted: false,
                Passed: false,
                Summary: "source discovery failed",
                Checks: [],
                Rejection: new FocusedEvidenceRejection(
                    FocusedEvidenceRejectionCode.SourceDiscoveryFailure,
                    " FullyQualifiedName~ConductorDriverTests.MissingMethod",
                    "source discovery failed")),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        var outcome = reviewer.VerificationHistory.Last().MergedReviewFindings!
            .Single(item => item.StableId == "source-discovery-apparatus")
            .EvidenceOutcome;
        Assert.False(outcome?.Honoured);
        Assert.Equal(FindingEvidenceNotHonouredReason.SelectionApparatusFailure, outcome?.Reason);
    }

    [Xunit.Fact]
    public void FindingEvidenceRawLengthCannotBeTrimmedBelowBound()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var paddedFilter = "ConductorDriverTests" + new string(' ', 1024);
        var finding = EvidenceFindingWithRequest(
            "Whitespace padding must not bypass the selection bound.",
            id: "raw-filter-bound",
            classes: [paddedFilter]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence required", findings: [finding]);
        var focusedRuns = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult(request, true, true, "unexpected", []);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(0, focusedRuns);
        Assert.Equal(
            FindingEvidenceNotHonouredReason.UnparseableSelection,
            reviewer.VerificationHistory.Last().MergedReviewFindings!.Single().EvidenceOutcome?.Reason);
    }

    [Xunit.Fact]
    public void FocusedSelectionApparatusFailurePreservesReceiptAndRetriesRequesterOnly()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = EvidenceFindingWithRequest(
            "The selection must execute at least one test.",
            id: "zero-selection",
            classes: ["GoalAcceptanceVerifierTests"]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "focused evidence required", findings: [finding]);
        var retriedTaskIds = new List<TaskId>();
        var check = new AcceptanceCheckResult(
            "reviewer focused evidence",
            Passed: false,
            ExitCode: 8,
            OutputTail: "0 tests were selected",
            TestResultPaths: ["C:\\receipts\\zero.trx"],
            FailureClassification: AcceptanceFailureClassifications.FocusedSelectionApparatusFailure,
            ExecutedTestCount: 0);
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) => new FocusedEvidenceRunResult(
                request,
                Accepted: true,
                Passed: false,
                Summary: "focused selection apparatus failure",
                Checks: [check],
                Arms:
                [
                    new FocusedEvidenceArmRunResult(
                        FindingEvidenceArm.Candidate,
                        "abc1234",
                        FindingEvidenceArmDisposition.ApparatusFailure,
                        Accepted: true,
                        Passed: false,
                        "executed=0",
                        [check])
                ],
                OutcomeReason: FindingEvidenceOutcomeReason.ApparatusFailure),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskIds.Add(taskId);
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceRun: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRun(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal([reviewer.Id], retriedTaskIds);
        Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        Assert.Equal(WorkTaskStatus.Completed, tester.Status);
        var outcome = reviewer.VerificationHistory.Last().MergedReviewFindings!.Single().EvidenceOutcome;
        Assert.False(outcome?.Honoured);
        Assert.Equal(FindingEvidenceNotHonouredReason.SelectionApparatusFailure, outcome?.Reason);
        Assert.Equal(FindingEvidenceOutcomeReason.ApparatusFailure, outcome?.ResultReason);
        Assert.NotNull(outcome?.ReceiptId);
        var receipt = Assert.Single(reviewer.VerificationHistory.Last().FindingEvidenceReceipts!);
        Assert.Equal(FindingEvidenceArmDisposition.ApparatusFailure, receipt.Arms!.Single().Disposition);
        Assert.Contains("zero.trx", receipt.Arms.Single().ReceiptPaths!.Single(), StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void EquivalentRequestsRunOnceAndShareReceiptIdentity()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var first = EvidenceFindingWithRequest(
            "First finding needs the shared run.",
            id: "first-request",
            classes: ["GoalAcceptanceVerifierTests", "ConductorDriverTests"]);
        var second = EvidenceFindingWithRequest(
            "Second finding needs the same shared run.",
            id: "second-request",
            classes: ["ConductorDriverTests", "GoalAcceptanceVerifierTests", "ConductorDriverTests"]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "shared evidence required", findings: [first, second]);
        var focusedRuns = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                Assert.Equal(
                    "Infrastructure.Tests:ConductorDriverTests; " +
                    "Infrastructure.Tests:GoalAcceptanceVerifierTests",
                    request);
                return DualArmFindingEvidence(request, FindingEvidenceArmDisposition.Red);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceRun: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRun(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        var recorded = reviewer.VerificationHistory.Last().MergedReviewFindings!;
        var firstReceipt = recorded.Single(finding => finding.StableId == "first-request").EvidenceOutcome?.ReceiptId;
        var secondReceipt = recorded.Single(finding => finding.StableId == "second-request").EvidenceOutcome?.ReceiptId;
        Assert.NotNull(firstReceipt);
        Assert.Equal(firstReceipt, secondReceipt);
        var receipt = Assert.Single(reviewer.VerificationHistory.Last().FindingEvidenceReceipts!);
        Assert.Equal(FindingEvidenceOutcomeReason.ValidEvidence,
            recorded.Single(finding => finding.StableId == "first-request").EvidenceOutcome?.ResultReason);
        Assert.Equal(firstReceipt, receipt.ReceiptId);
        Assert.Collection(
            receipt.Arms!,
            arm =>
            {
                Assert.Equal(FindingEvidenceArm.Candidate, arm.Arm);
                Assert.NotEmpty(arm.ReceiptPaths!);
            },
            arm =>
            {
                Assert.Equal(FindingEvidenceArm.Baseline, arm.Arm);
                Assert.NotEmpty(arm.ReceiptPaths!);
                Assert.NotEmpty(arm.FailingTestIdentities!);
            });
        Assert.Contains(
            "failing_tests: EvidenceTests.RejectsBaseline",
            kernel.BuildTaskBrief(goal.Id, reviewer.Id).Content,
            StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void CandidateAndBaselineGreenRecordsClosedVacuousEvidenceOutcome()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            "vacuous evidence must be explicit",
            findings: [EvidenceFindingWithRequest("Run an always-green check.", id: "vacuous")]);
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
                DualArmFindingEvidence(request, FindingEvidenceArmDisposition.Green),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        var recorded = reviewer.VerificationHistory.Last().MergedReviewFindings!.Single();
        Assert.True(recorded.EvidenceOutcome?.Honoured);
        Assert.Equal(FindingEvidenceOutcomeReason.VacuousEvidence, recorded.EvidenceOutcome?.ResultReason);
        var receipt = Assert.Single(reviewer.VerificationHistory.Last().FindingEvidenceReceipts!);
        Assert.False(receipt.Passed);
        Assert.All(receipt.Arms!, arm => Assert.Equal(FindingEvidenceArmDisposition.Green, arm.Disposition));
        Assert.Contains("reason=vacuous-evidence", kernel.BuildTaskBrief(goal.Id, reviewer.Id).Content, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void BaselineBuildFailureRecordsInconclusiveRatherThanRed()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            "baseline must execute",
            findings: [EvidenceFindingWithRequest("Baseline is structurally broken.", id: "baseline-build")]);
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
                DualArmFindingEvidence(request, FindingEvidenceArmDisposition.Inconclusive),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        var recorded = reviewer.VerificationHistory.Last().MergedReviewFindings!.Single();
        Assert.Equal(FindingEvidenceOutcomeReason.BaselineInconclusive, recorded.EvidenceOutcome?.ResultReason);
        var baseline = Assert.Single(
            reviewer.VerificationHistory.Last().FindingEvidenceReceipts!.Single().Arms!,
            arm => arm.Arm == FindingEvidenceArm.Baseline);
        Assert.Equal(FindingEvidenceArmDisposition.Inconclusive, baseline.Disposition);
        Assert.NotEqual(FindingEvidenceArmDisposition.Red, baseline.Disposition);
    }

    [Xunit.Fact]
    public void CompatibleSameProjectRequestsRunInOneBatch()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var findings = Enumerable.Range(1, 4)
            .Select(index => EvidenceFindingWithRequest(
                $"Finding {index} needs focused evidence.",
                id: $"request-{index}",
                classes: [$"FocusedEvidence{index}Tests"]))
            .ToArray();
        FailReviewerNeedsWork(kernel, goal, reviewer, "four distinct evidence requests", findings: findings);
        var requests = new List<string>();
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                requests.Add(request);
                return DualArmFindingEvidence(request, FindingEvidenceArmDisposition.Red);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(
            [
                "Infrastructure.Tests:FocusedEvidence1Tests; " +
                "Infrastructure.Tests:FocusedEvidence2Tests; " +
                "Infrastructure.Tests:FocusedEvidence3Tests; " +
                "Infrastructure.Tests:FocusedEvidence4Tests"
            ],
            requests);
        Assert.Single(requests);
        var recorded = reviewer.VerificationHistory.Last().MergedReviewFindings!;
        foreach (var stableId in new[] { "request-1", "request-2", "request-3", "request-4" })
        {
            var outcome = recorded.Single(finding => finding.StableId == stableId).EvidenceOutcome;
            Assert.True(outcome?.Honoured);
        }
        var receipt = Assert.Single(reviewer.VerificationHistory.Last().FindingEvidenceReceipts!);
        Assert.All(receipt.RequestDispositions!, disposition =>
            Assert.Equal("executed-batched", disposition.Disposition));
    }

    [Xunit.Fact]
    public void IncompatibleSameProjectRequestsRecordTypedStandaloneAndPendingReasons()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            "incompatible same-project evidence requests",
            findings:
            [
                EvidenceFindingWithRequest("Bare class request.", id: "bare", classes: ["ConductorDriverTests"]),
                EvidenceFindingWithRequest(
                    "Expression request.",
                    id: "expression",
                    classes: ["FullyQualifiedName~GoalAcceptanceVerifierTests"])
            ]);
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
                DualArmFindingEvidence(request, FindingEvidenceArmDisposition.Green),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        var receipt = Assert.Single(reviewer.VerificationHistory.Last().FindingEvidenceReceipts!);
        Assert.Contains(receipt.RequestDispositions!, disposition =>
            disposition.FindingStableId == "bare" &&
            disposition.Disposition == "executed-standalone" &&
            disposition.Reason == "incompatible-filter-semantics");
        Assert.Contains(receipt.RequestDispositions!, disposition =>
            disposition.FindingStableId == "expression" &&
            disposition.Disposition == "pending-standalone" &&
            disposition.Reason == "incompatible-filter-semantics");
    }

    [Xunit.Fact]
    public void ActionableCandidateRedSupersedesRemainingRoundAndRetriesDeveloper()
    {
        const string candidateSha = "abc1234";
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task, hasCommittedChanges: task == developer);
        }

        var first = EvidenceFindingWithRequest(
            "The first Developer-owned fixture needs focused evidence.",
            id: "fixture-red",
            classes: ["GateReadyCandidateProjectorTests"]);
        var compatible = EvidenceFindingWithRequest(
            "A compatible request should share the project invocation.",
            id: "same-project",
            classes: ["ConductorDriverTests"]);
        var pending = EvidenceFindingWithRequest(
            "A distinct project request must not run after the actionable RED.",
            id: "pending-core",
            project: "Core.Tests",
            classes: ["GoalLifecycleTests"]);
        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            "candidate RED must fail fast",
            findings: [first, compatible, pending]);

        var focusedRuns = new List<string>();
        TaskId? retriedTaskId = null;
        string? retryMessage = null;
        var dispatchStarts = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns.Add(request);
                return CandidateRedFindingEvidence(request, candidateSha);
            },
            dispatchAndStart: _ =>
            {
                dispatchStarts++;
                return DispatchStartOutcome.Started();
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskId = taskId;
                retryMessage = message;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceRun: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRun(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Equal(developer.Id, retriedTaskId);
        Assert.Equal(1, dispatchStarts);
        var request = Assert.Single(focusedRuns);
        Assert.Equal(
            "Infrastructure.Tests:ConductorDriverTests; " +
            "Infrastructure.Tests:GateReadyCandidateProjectorTests",
            request);
        Assert.Contains("ACTIONABLE_CANDIDATE_RED", retryMessage, StringComparison.Ordinal);
        Assert.Contains(candidateSha, retryMessage, StringComparison.Ordinal);
        Assert.Contains(
            "GateReadyCandidateProjectorTests.DefaultHarnessProducesSerializedResourceKey",
            retryMessage,
            StringComparison.Ordinal);

        var recorded = reviewer.VerificationHistory.Last().MergedReviewFindings!;
        var pendingOutcome = recorded.Single(finding => finding.StableId == "pending-core").EvidenceOutcome;
        Assert.False(pendingOutcome?.Honoured);
        Assert.Equal(FindingEvidenceNotHonouredReason.SupersededByActionableRed, pendingOutcome?.Reason);
        Assert.Equal(FindingEvidenceOutcomeReason.CandidateRed, pendingOutcome?.ResultReason);
        var receipt = Assert.Single(reviewer.VerificationHistory.Last().FindingEvidenceReceipts!);
        Assert.Equal(candidateSha, receipt.CandidateSha);
        Assert.Contains(receipt.RequestDispositions!, disposition =>
            disposition.FindingStableId == "pending-core" &&
            disposition.Reason == "superseded-by-actionable-red");
        Assert.Equal(WorkTaskStatus.Assigned, tester.Status);
        Assert.Equal(WorkTaskStatus.Assigned, reviewer.Status);
    }

    [Xunit.Fact]
    public void ActionableRedRunningDownstreamEscalatesWithReceiptWithoutDispatch()
    {
        const string candidateSha = "abc1234";
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task, hasCommittedChanges: task.RequiredRole == AgentRole.Developer);
        }

        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            "candidate RED collides with a running downstream task",
            findings:
            [
                EvidenceFindingWithRequest(
                    "Developer-owned fixture is RED.",
                    id: "fixture-red",
                    classes: ["GateReadyCandidateProjectorTests"])
            ]);
        var dispatchStarts = 0;
        string? escalationReason = null;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            runFocusedEvidence: (_, request) => CandidateRedFindingEvidence(request, candidateSha),
            dispatchAndStart: _ =>
            {
                dispatchStarts++;
                return DispatchStartOutcome.Started();
            },
            retryTaskWithRoundKind: (_, _, _, _) => throw new InvalidOperationException(
                "Cannot retry Developer task while downstream Tester task has a running process."),
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceRun: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRun(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt),
            writeEscalation: (_, _, reason) => escalationReason = reason);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Assert.Equal(0, dispatchStarts);
        Assert.Contains("ACTIONABLE_CANDIDATE_RED_LIFECYCLE_CONFLICT", escalationReason, StringComparison.Ordinal);
        Assert.Contains(candidateSha, escalationReason, StringComparison.Ordinal);
        var receipt = Assert.Single(reviewer.VerificationHistory.Last().FindingEvidenceReceipts!);
        Assert.Contains(receipt.ReceiptId, escalationReason, StringComparison.Ordinal);
        Assert.Contains("running process", escalationReason, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact]
    public void MixedBatchCandidateRedRoutesOnlyWhenFailingIdentityMatchesDeveloperOwnedRequest()
    {
        const string candidateSha = "abc1234";
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var operatorOwned = EvidenceFindingWithRequest(
            "The operator-owned request selects the failing test.",
            id: "operator-request",
            category: FindingCategory.OperatorOwned,
            classes: ["GateReadyCandidateProjectorTests"]);
        var developerOwned = EvidenceFindingWithRequest(
            "The Developer-owned request selects a different test.",
            id: "developer-request",
            classes: ["ConductorDriverTests"]);
        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            "candidate RED attribution must stay request-bound",
            findings: [operatorOwned, developerOwned]);

        TaskId? retriedTaskId = null;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            runFocusedEvidence: (_, request) => CandidateRedFindingEvidence(request, candidateSha),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskId = taskId;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(reviewer.Id, retriedTaskId);
        Assert.DoesNotContain(goal.Timeline, evt =>
            evt.Kind == ProgressKind.TaskRetried &&
            evt.Message.Contains("ACTIONABLE_CANDIDATE_RED", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void CandidateRedAttributionRequiresTestClassBoundary()
    {
        const string candidateSha = "abc1234";
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            "candidate RED attribution must respect class boundaries",
            findings:
            [
                EvidenceFindingWithRequest(
                    "Operator request owns the actual failure.",
                    id: "operator-request",
                    category: FindingCategory.OperatorOwned,
                    classes: ["ConductorDriverTests2"]),
                EvidenceFindingWithRequest(
                    "Developer request is only a name prefix.",
                    id: "developer-request",
                    classes: ["ConductorDriverTests"])
            ]);

        TaskId? retriedTaskId = null;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            runFocusedEvidence: (_, request) => CandidateRedFindingEvidence(
                request,
                candidateSha,
                "ConductorDriverTests2.FailingMember"),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskId = taskId;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(reviewer.Id, retriedTaskId);
        Assert.DoesNotContain(goal.Timeline, evt =>
            evt.Kind == ProgressKind.TaskRetried &&
            evt.Message.Contains("ACTIONABLE_CANDIDATE_RED", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void StaleFindingEvidenceReceiptDoesNotSuppressRerunAfterCandidateChanges()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = EvidenceFindingWithRequest(
            "Focused evidence must bind to the current candidate.",
            id: "candidate-bound");
        FailReviewerNeedsWork(kernel, goal, reviewer, "candidate-bound evidence", findings: [finding]);
        var candidateSha = "abc1234";
        var focusedRuns = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return DualArmFindingEvidence(request, FindingEvidenceArmDisposition.Red);
            },
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        candidateSha = "def5678";
        FailReviewerNeedsWork(kernel, goal, reviewer, "candidate-bound evidence", findings: [finding]);

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(2, focusedRuns);
        var receipts = reviewer.VerificationHistory
            .SelectMany(verification => verification.FindingEvidenceReceipts ?? [])
            .DistinctBy(receipt => receipt.ReceiptId)
            .ToArray();
        Assert.Equal(2, receipts.Length);
        Assert.Contains(receipts, receipt => receipt.CandidateSha == "abc1234");
        Assert.Contains(receipts, receipt => receipt.CandidateSha == "def5678");
    }

    [Xunit.Fact]
    public void ChangedFindingRoundAtSameCandidateDoesNotReusePriorReceipt()
    {
        const string candidateSha = "abc1234";
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = EvidenceFindingWithRequest(
            "The first finding round needs focused evidence.",
            id: "same-sha-new-round");
        FailReviewerNeedsWork(kernel, goal, reviewer, "first finding round", findings: [finding]);
        var focusedRuns = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext(candidateSha),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return DualArmFindingEvidence(request, FindingEvidenceArmDisposition.Red);
            },
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        var repeatedFinding = finding with { Description = "A changed finding round still needs focused evidence." };
        FailReviewerNeedsWork(kernel, goal, reviewer, "changed finding round", findings: [repeatedFinding]);

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(2, focusedRuns);
        var receipts = reviewer.VerificationHistory
            .SelectMany(verification => verification.FindingEvidenceReceipts ?? [])
            .DistinctBy(receipt => receipt.ReceiptId)
            .ToArray();
        Assert.Equal(2, receipts.Length);
        Assert.All(receipts, receipt => Assert.Equal(candidateSha, receipt.CandidateSha));
        Assert.Equal(2, receipts.Select(receipt => receipt.FindingRoundFingerprint).Distinct().Count());
    }

    [Xunit.Theory]
    [Xunit.InlineData("Unsupported.Tests")]
    [Xunit.InlineData("Mcg.AgentOrchestrator.App")]
    public void InvalidAndValidRequestsAreProcessedIndependently(string unsupportedProject)
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var invalid = EvidenceFindingWithRequest(
            "Unsupported project should be refused.",
            id: "invalid-request",
            project: unsupportedProject,
            classes: ["UnknownTests"]);
        var valid = EvidenceFindingWithRequest(
            "Valid request should still run.",
            id: "valid-request",
            classes: ["ConductorDriverTests"]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "mixed evidence requests", findings: [invalid, valid]);
        var focusedRuns = 0;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult(request, true, true, "valid evidence passed", []);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceRequest: (goalId, taskId, message) =>
                kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        var recorded = reviewer.VerificationHistory.Last().MergedReviewFindings!;
        var invalidOutcome = recorded.Single(finding => finding.StableId == "invalid-request").EvidenceOutcome;
        var validOutcome = recorded.Single(finding => finding.StableId == "valid-request").EvidenceOutcome;
        Assert.False(invalidOutcome?.Honoured);
        Assert.Equal(FindingEvidenceNotHonouredReason.UnsupportedProject, invalidOutcome?.Reason);
        Assert.Contains(unsupportedProject, invalidOutcome?.Detail, StringComparison.Ordinal);
        Assert.Contains("Accepted forms: Core, Core.Tests", invalidOutcome?.Detail, StringComparison.Ordinal);
        Assert.Contains("Mcg.AgentOrchestrator.Infrastructure.Tests", invalidOutcome?.Detail, StringComparison.Ordinal);
        Assert.Contains("full .csproj path", invalidOutcome?.Detail, StringComparison.Ordinal);
        Assert.Null(invalidOutcome?.ReceiptId);
        Assert.Contains(goal.Timeline, evt =>
            evt.Kind == ProgressKind.FindingEvidenceRequestRecorded &&
            evt.Message.Contains("finding_id=invalid-request", StringComparison.Ordinal) &&
            evt.Message.Contains("candidate_sha=abc1234", StringComparison.Ordinal));
        Assert.True(validOutcome?.Honoured);
        Assert.NotNull(validOutcome?.ReceiptId);
        Assert.Null(validOutcome?.Reason);
        var retryBrief = kernel.BuildTaskBrief(goal.Id, reviewer.Id).Content;
        Assert.Contains("reason=unsupported-project", retryBrief, StringComparison.Ordinal);
        Assert.Contains("valid evidence passed", retryBrief, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void FindingEvidenceRequestAcceptsRegisteredCliInfrastructureProject()
    {
        const string cliProject =
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Cli/" +
            "Mcg.AgentOrchestrator.Infrastructure.Cli.Tests.csproj";
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = EvidenceFindingWithRequest(
            "The extracted CLI project needs focused evidence.",
            id: "cli-extracted-project",
            project: "Infrastructure.Cli.Tests",
            classes: ["CliArgumentNormalizationTests", "CliCommandTestsAddTaskCommands"]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "registered extracted project", findings: [finding]);
        string? request = null;
        var driver = MakeDriver(
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            getFindingEvidenceEngineSettings: _ => new AcceptanceGateEngineSettings
            {
                MtpInvocations = [new AcceptanceMtpInvocation { Project = cliProject }]
            },
            runFocusedEvidence: (_, value) =>
            {
                request = value;
                return new FocusedEvidenceRunResult(value, true, true, "registered project passed", []);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(
            "Infrastructure.Cli.Tests:CliArgumentNormalizationTests; Infrastructure.Cli.Tests:CliCommandTestsAddTaskCommands",
            request);
        var recorded = reviewer.VerificationHistory.Last().MergedReviewFindings!;
        Assert.True(recorded.Single(item => item.StableId == "cli-extracted-project").EvidenceOutcome?.Honoured);
    }

    [Xunit.Fact]
    public void ResolvedFindingEvidenceRequestDoesNotRun()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var open = EvidenceFindingWithRequest(
            "Historical request is already resolved.",
            id: "resolved-request",
            classes: ["ConductorDriverTests"]);
        FailReviewerNeedsWork(kernel, goal, reviewer, "historical finding was open", findings: [open]);
        kernel.RetryTask(goal.Id, reviewer.Id, "recheck resolved finding");
        var resolved = open with
        {
            State = ReviewFindingState.Resolved
        };
        FailReviewerNeedsWork(kernel, goal, reviewer, "historical finding was resolved", findings: [resolved]);
        var focusedRuns = 0;
        var driver = MakeDriver(
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult(request, true, true, "must not run", []);
            },
            retryTask: (goalId, taskId, message) => kernel.RetryTask(goalId, taskId, message));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(0, focusedRuns);
        Assert.Null(reviewer.VerificationHistory.Last().MergedReviewFindings!
            .Single(finding => finding.StableId == "resolved-request").EvidenceOutcome);
    }

    [Xunit.Fact]
    public void NotHonouredDetailTextDoesNotChangeRoutingDecision()
    {
        var retriedRoles = new List<AgentRole>();
        foreach (var detail in new[] { "first display-only detail", "completely different human wording" })
        {
            var (kernel, goal) = SoftwareGoal();
            var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
            {
                PassVerification(kernel, goal, task);
            }
            var finding = EvidenceFindingWithRequest("Evidence is unavailable.", id: "detail-only");
            FailReviewerNeedsWork(kernel, goal, reviewer, "evidence unavailable", findings: [finding]);
            kernel.RecordFindingEvidenceOutcome(
                goal.Id,
                reviewer.Id,
                finding.StableId,
                new FindingEvidenceOutcome(
                    Honoured: false,
                    Reason: FindingEvidenceNotHonouredReason.ExecutorUnavailable,
                    Detail: detail));
            var driver = MakeDriver(
                retryTask: (goalId, taskId, message) =>
                {
                    var retried = kernel.RetryTask(goalId, taskId, message);
                    retriedRoles.Add(retried.RequiredRole);
                    return retried;
                });

            driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        }

        Assert.Equal([AgentRole.Tester, AgentRole.Tester], retriedRoles);
    }

    [Xunit.Fact]
    public void ProseOnlyEvidenceNamesRouteNormallyWithoutRunning()
    {
        var (kernel, goal) = SoftwareGoal();
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        const string blocker =
            "Infrastructure.Tests FirstReceiptTests SecondReceiptTests ThirdReceiptTests " +
            "FourthReceiptTests FifthReceiptTests receipts are missing.";
        FailReviewerNeedsWork(kernel, goal, reviewer, blocker, findings: [EvidenceFinding(blocker)]);
        var focusedRuns = 0;
        TaskId? retriedTaskId = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runFocusedEvidence: (_, _) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult("", true, true, "should not run", []);
            },
            retryTask: (goalId, taskId, message) =>
            {
                retriedTaskId = taskId;
                return kernel.RetryTask(goalId, taskId, message);
            },
            recordReviewerEvidenceRequestReceived: (goalId, taskId, message) =>
                kernel.RecordReviewerEvidenceRequestReceived(goalId, taskId, message));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(0, focusedRuns);
        Assert.Equal(tester.Id, retriedTaskId);
        Assert.DoesNotContain(goal.Timeline, evt =>
            evt.Kind == ProgressKind.ReviewerEvidenceRequestReceived);
    }

    [Xunit.Fact]
    public void ExistingDerivedReceipt_RoutesTesterWithoutRerun()
    {
        var (kernel, goal) = SoftwareGoal();
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        const string blocker = "Infrastructure.Tests ConductorDriverTests receipt is missing.";
        FailReviewerNeedsWork(kernel, goal, reviewer, blocker, findings: [EvidenceFinding(blocker)]);
        var focusedRuns = 0;
        TaskId? retriedTaskId = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, _) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult("", true, true, "should not run", []);
            },
            retryTask: (goalId, taskId, message) =>
            {
                retriedTaskId = taskId;
                return kernel.RetryTask(goalId, taskId, message);
            },
            recordReviewerEvidenceRequestReceived: (goalId, taskId, message) =>
                kernel.RecordReviewerEvidenceRequestReceived(goalId, taskId, message));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(0, focusedRuns);
        Assert.Equal(tester.Id, retriedTaskId);
    }

    [Xunit.Fact]
    public void SubstitutionCap_SurvivesSnapshotReload()
    {
        var (kernel, originalGoal) = SoftwareGoal();
        var originalReviewer = originalGoal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in originalGoal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, originalGoal, task);
        }

        kernel = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        var goal = kernel.GetGoal(originalGoal.Id);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        const string blocker = "Infrastructure.Tests GoalAcceptanceVerifierTests receipt is missing.";
        FailReviewerNeedsWork(kernel, goal, reviewer, blocker, findings: [EvidenceFinding(blocker)]);
        TaskId? retriedTaskId = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            getPreReviewEvidenceContext: _ => NoPreReviewContext("def5678"),
            retryTask: (goalId, taskId, message) =>
            {
                retriedTaskId = taskId;
                return kernel.RetryTask(goalId, taskId, message);
            },
            recordReviewerEvidenceRequestReceived: (goalId, taskId, message) =>
                kernel.RecordReviewerEvidenceRequestReceived(goalId, taskId, message));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(tester.Id, retriedTaskId);
    }

    [Xunit.Fact]
    public void FailingDerivedEvidence_RetriesReviewerWithReceipt()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        const string blocker = "Core.Tests GoalLifecycleTests receipt is missing.";
        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            blocker,
            findings: [EvidenceFindingWithRequest(blocker, project: "Core.Tests", classes: ["GoalLifecycleTests"])]);
        TaskId? retriedTaskId = null;
        string? retryMessage = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) => new FocusedEvidenceRunResult(
                request,
                Accepted: true,
                Passed: false,
                Summary: "focused evidence failed; receipts: C:\\tmp\\failed-derived-trx",
                Checks: []),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskId = taskId;
                retryMessage = message;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            recordReviewerEvidenceRequestReceived: (goalId, taskId, message) =>
                kernel.RecordReviewerEvidenceRequestReceived(goalId, taskId, message),
            recordReviewerEvidenceRunRecorded: (goalId, taskId, message) =>
                kernel.RecordReviewerEvidenceRunRecorded(goalId, taskId, message),
            recordPreReviewEvidence: (goalId, taskId, receipt) =>
                kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(reviewer.Id, retriedTaskId);
        var failedReceipt = Assert.Single(reviewer.VerificationHistory.SelectMany(item => item.FindingEvidenceReceipts ?? []));
        Assert.False(failedReceipt.Passed);
        Assert.Contains("C:\\tmp\\failed-derived-trx", failedReceipt.Summary, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void MissingStructuredRequestDoesNotDeriveFromProse()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var blocker = "Required receipts: Infrastructure.Tests ConductorDriverTests and GoalAcceptanceVerifierTests.";
        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            blocker,
            findings:
            [
                new ReviewFinding(
                    "missing-receipts",
                    ReviewFindingState.Open,
                    new ReviewFindingLocation("tests/receipts", "focused"),
                    blocker,
                    FindingSeverity.Blocking,
                    FindingCategory.TestEvidence),
                new ReviewFinding(
                    "resolved-correctness",
                    ReviewFindingState.Resolved,
                    new ReviewFindingLocation("src/Resolved.cs", "Run"),
                    "resolved correctness finding must not block substitution",
                    FindingSeverity.Blocking,
                    FindingCategory.Correctness),
                new ReviewFinding(
                    "advisory-correctness",
                    ReviewFindingState.Open,
                    new ReviewFindingLocation("src/Advisory.cs", "Run"),
                    "advisory correctness finding must not block substitution",
                    FindingSeverity.Advisory,
                    FindingCategory.Correctness)
            ]);
        string? focusedRequest = null;
        var retriedTaskIds = new List<TaskId>();
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                focusedRequest = request;
                return new FocusedEvidenceRunResult(request, true, true, "passed; receipts: C:\\tmp\\derived-trx", []);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskIds.Add(taskId);
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            recordReviewerEvidenceRequestReceived: (goalId, taskId, message) =>
                kernel.RecordReviewerEvidenceRequestReceived(goalId, taskId, message),
            recordReviewerEvidenceRunRecorded: (goalId, taskId, message) =>
                kernel.RecordReviewerEvidenceRunRecorded(goalId, taskId, message),
            recordPreReviewEvidence: (goalId, taskId, receipt) =>
                kernel.RecordPreReviewEvidence(goalId, taskId, receipt));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Null(focusedRequest);
        Assert.Equal([goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester).Id], retriedTaskIds);
        Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        Assert.DoesNotContain(goal.Timeline, evt =>
            evt.TaskId == reviewer.Id &&
            evt.Kind == ProgressKind.ReviewerEvidenceRequestReceived);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact]
    public void PreReviewEvidence_DualArmVacuousCandidateGreen_AllowsReviewerDispatch()
    {
        var (kernel, goal) = SoftwareGoal();
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
        {
            PassVerification(kernel, goal, task);
        }

        var focusedRuns = 0;
        var dispatches = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => FocusedPreReviewContext("abc123"),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                Assert.Contains("ConductorDriverTests", request, StringComparison.Ordinal);
                return DualArmFindingEvidence(request, FindingEvidenceArmDisposition.Green);
            },
            recordPreReviewEvidence: (goalId, taskId, receipt) =>
                kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
            dispatchAndStart: _ =>
            {
                Assert.Equal(PreReviewEvidenceDisposition.Green, reviewer.PreReviewEvidenceReceipt?.Disposition);
                dispatches++;
                return DispatchStartOutcome.Started();
            });

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        Assert.Equal(1, dispatches);
        Assert.Equal("abc123", reviewer.PreReviewEvidenceReceipt?.CandidateSha);
    }

    [Xunit.Fact]
    public void PreReviewEvidence_RejectedResultWithoutOutcomeReportsUnknown()
    {
        var (kernel, goal) = SoftwareGoal();
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
        {
            PassVerification(kernel, goal, task);
        }

        string? retryMessage = null;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => FocusedPreReviewContext("rejected-sha"),
            runFocusedEvidence: (_, request) => new FocusedEvidenceRunResult(
                request,
                Accepted: false,
                Passed: false,
                Summary: "focused evidence request rejected",
                Checks: []),
            recordPreReviewEvidence: (goalId, taskId, receipt) =>
                kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retryMessage = message;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            });

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(WorkTaskStatus.Assigned, tester.Status);
        Assert.Contains("outcome=unknown", retryMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("outcome=valid-evidence", retryMessage, StringComparison.Ordinal);
    }

    [Xunit.Fact(Timeout = 30_000)]
    [Xunit.Trait("Category", "CrossTick")]
    public void PreReviewEvidence_InFlightRun_ReturnsThenReconcilesAfterRestart()
    {
        var root = CreateTempDirectory();
        try
        {
            var (kernel, goal) = SoftwareGoal();
            var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
            {
                PassVerification(kernel, goal, task);
            }

            var completionGate = new ConductorParallelAcceptanceAttemptCompletionGateForTests();
            var attemptRoot = Path.Combine(root, "pre-review-evidence-attempts");
            var startingCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                isProcessAlive: _ => true,
                attemptCompletionGateForTests: completionGate,
                acquireStableSlotLease: (_, _) => null);
            var focusedRuns = 0;
            var dispatches = 0;
            FocusedEvidenceRunResult RunEvidence(Goal _, string request)
            {
                focusedRuns++;
                return PassingPreReviewEvidence(request);
            }

            var startingDriver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getPreReviewEvidenceContext: _ => FocusedPreReviewContext("async-sha"),
                runFocusedEvidence: RunEvidence,
                recordPreReviewEvidence: (goalId, taskId, receipt) =>
                    kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
                dispatchAndStart: _ =>
                {
                    dispatches++;
                    return DispatchStartOutcome.Started();
                },
                focusedEvidenceAttemptCoordinator: startingCoordinator);

            var scheduled = startingDriver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(scheduled.Outcome);
            var heldAttempt = completionGate.RequiredHandleForTests(goal.Id.Value);
            Assert.Equal($"pre-review-evidence:{heldAttempt.Attempt.AttemptId}", held.StableIdentity);
            Assert.Equal(0, focusedRuns);
            Assert.Equal(0, dispatches);
            Assert.Equal(1, completionGate.HeldCount);

            heldAttempt.CompleteForTests();
            Assert.Equal(1, focusedRuns);

            var restartedCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                isProcessAlive: _ => false,
                launchOwnedProcess: _ => throw new InvalidOperationException("completed attempt must be reconciled"),
                acquireStableSlotLease: (_, _) => null);
            var restartedDriver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getPreReviewEvidenceContext: _ => FocusedPreReviewContext("async-sha"),
                runFocusedEvidence: RunEvidence,
                recordPreReviewEvidence: (goalId, taskId, receipt) =>
                    kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
                dispatchAndStart: _ =>
                {
                    dispatches++;
                    return DispatchStartOutcome.Started();
                },
                focusedEvidenceAttemptCoordinator: restartedCoordinator);

            var reconciled = restartedDriver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            Assert.IsType<ConductorAdvanceOutcome.Executed>(reconciled.Outcome);
            Assert.Equal(1, focusedRuns);
            Assert.Equal(1, dispatches);
            Assert.Equal(PreReviewEvidenceDisposition.Green, reviewer.PreReviewEvidenceReceipt?.Disposition);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Xunit.Fact(Timeout = 30_000)]
    [Xunit.Trait("Category", "CrossTick")]
    public void PostReviewEvidence_InFlightTypedRun_ReturnsThenReconcilesAfterRestart()
    {
        var root = CreateTempDirectory();
        try
        {
            var (kernel, goal) = SoftwareGoal();
            var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
            {
                PassVerification(kernel, goal, task);
            }

            const string request = "Infrastructure.Tests: ConductorDriverTests";
            const string blocker = "Infrastructure.Tests ConductorDriverTests receipt is missing.";
            FailReviewerNeedsWork(
                kernel,
                goal,
                reviewer,
                blocker,
                evidenceRequest: request);

            var completionGate = new ConductorParallelAcceptanceAttemptCompletionGateForTests();
            var attemptRoot = Path.Combine(root, "pre-review-evidence-attempts");
            var startingCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                isProcessAlive: _ => true,
                attemptCompletionGateForTests: completionGate,
                acquireStableSlotLease: (_, _) => null);
            var focusedRuns = 0;
            var retries = 0;
            FocusedEvidenceRunResult RunEvidence(Goal _, string request)
            {
                focusedRuns++;
                Assert.Equal("Infrastructure.Tests: ConductorDriverTests", request);
                return PassingPreReviewEvidence(request);
            }

            ConductorDriver MakePostReviewDriver(ConductorParallelAcceptanceAttemptCoordinator coordinator) =>
                MakeDriver(
                    getFacts: _ => GoalLifecycleFacts.None,
                    getPreReviewEvidenceContext: _ => NoPreReviewContext("def5678"),
                    runFocusedEvidence: RunEvidence,
                    retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
                    {
                        retries++;
                        return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
                    },
                    recordFindingEvidenceRequest: (goalId, taskId, message) =>
                        kernel.RecordFindingEvidenceRequest(goalId, taskId, message),
                    recordFindingEvidenceRun: (goalId, taskId, message) =>
                        kernel.RecordFindingEvidenceRun(goalId, taskId, message),
                    focusedEvidenceAttemptCoordinator: coordinator,
                    recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                        kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt));

            var startingDriver = MakePostReviewDriver(startingCoordinator);
            var scheduled = startingDriver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            Assert.IsType<ConductorAdvanceOutcome.Held>(scheduled.Outcome);
            Assert.Equal(0, focusedRuns);
            Assert.Equal(0, retries);
            Assert.Equal(1, completionGate.HeldCount);

            completionGate.RequiredHandleForTests(goal.Id.Value).CompleteForTests();
            Assert.Equal(1, focusedRuns);

            var restartedCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                isProcessAlive: _ => false,
                launchOwnedProcess: _ => throw new InvalidOperationException("completed attempt must be reconciled"),
                acquireStableSlotLease: (_, _) => null);
            var restartedDriver = MakePostReviewDriver(restartedCoordinator);

            var reconciled = restartedDriver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            Assert.IsType<ConductorAdvanceOutcome.Executed>(reconciled.Outcome);
            Assert.Equal(1, focusedRuns);
            Assert.Equal(1, retries);
            Assert.Equal(WorkTaskStatus.Assigned, reviewer.Status);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Xunit.Fact(Timeout = 30_000)]
    [Xunit.Trait("Category", "CrossTick")]
    public void PreReviewEvidence_ProcessDiesAfterRestart_RelaunchesWithoutReceipt()
    {
        var root = CreateTempDirectory();
        try
        {
            var (kernel, goal) = SoftwareGoal();
            var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
            {
                PassVerification(kernel, goal, task);
            }

            var now = new DateTimeOffset(2026, 8, 3, 19, 0, 0, TimeSpan.Zero);
            var attemptRoot = Path.Combine(root, "pre-review-evidence-attempts");
            var launches = 0;
            var focusedRuns = 0;
            var dispatches = 0;
            var startingCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                utcNow: () => now,
                isProcessAlive: _ => true,
                launchOwnedProcess: _ =>
                {
                    launches++;
                    return new ConductorParallelAcceptanceOwnedProcessLaunchResult(7301);
                });
            var startingDriver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getPreReviewEvidenceContext: _ => FocusedPreReviewContext("restart-sha"),
                runFocusedEvidence: (_, request) =>
                {
                    focusedRuns++;
                    return PassingPreReviewEvidence(request);
                },
                recordPreReviewEvidence: (goalId, taskId, receipt) =>
                    kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
                dispatchAndStart: _ =>
                {
                    dispatches++;
                    return DispatchStartOutcome.Started();
                },
                focusedEvidenceAttemptCoordinator: startingCoordinator);

            var scheduled = startingDriver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            Assert.IsType<ConductorAdvanceOutcome.Held>(scheduled.Outcome);
            Assert.Equal(1, launches);

            now = now.AddMinutes(1);
            var restartedCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                utcNow: () => now,
                isProcessAlive: _ => false,
                launchOwnedProcess: _ =>
                {
                    launches++;
                    return new ConductorParallelAcceptanceOwnedProcessLaunchResult(7302);
                },
                recentHeartbeatGrace: TimeSpan.Zero);
            var restartedDriver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getPreReviewEvidenceContext: _ => FocusedPreReviewContext("restart-sha"),
                runFocusedEvidence: (_, request) =>
                {
                    focusedRuns++;
                    return PassingPreReviewEvidence(request);
                },
                recordPreReviewEvidence: (goalId, taskId, receipt) =>
                    kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
                dispatchAndStart: _ =>
                {
                    dispatches++;
                    return DispatchStartOutcome.Started();
                },
                focusedEvidenceAttemptCoordinator: restartedCoordinator);

            var recovered = restartedDriver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(recovered.Outcome);
            Assert.Contains("did not run (ProcessDied)", held.Reason, StringComparison.Ordinal);
            Assert.Null(reviewer.PreReviewEvidenceReceipt);
            Assert.Equal(1, launches);
            Assert.Equal(0, focusedRuns);
            Assert.Equal(0, dispatches);

            var relaunched = restartedDriver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            Assert.IsType<ConductorAdvanceOutcome.Held>(relaunched.Outcome);
            Assert.Equal(2, launches);
            Assert.Null(reviewer.PreReviewEvidenceReceipt);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Xunit.Fact(Timeout = 30_000)]
    [Xunit.Trait("Category", "CrossTick")]
    public void PreReviewEvidence_FaultedRun_EscalatesWithoutMappingReceipt()
    {
        var root = CreateTempDirectory();
        try
        {
            var (kernel, goal) = SoftwareGoal();
            var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
            {
                PassVerification(kernel, goal, task);
            }

            var completionGate = new ConductorParallelAcceptanceAttemptCompletionGateForTests();
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(root, "pre-review-evidence-attempts"),
                isProcessAlive: _ => true,
                attemptCompletionGateForTests: completionGate,
                acquireStableSlotLease: (_, _) => null);
            var focusedRuns = 0;
            var dispatches = 0;
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getPreReviewEvidenceContext: _ => FocusedPreReviewContext("fault-sha"),
                runFocusedEvidence: (_, _) =>
                {
                    focusedRuns++;
                    throw new InvalidOperationException("simulated evidence fault");
                },
                recordPreReviewEvidence: (goalId, taskId, receipt) =>
                    kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
                dispatchAndStart: _ =>
                {
                    dispatches++;
                    return DispatchStartOutcome.Started();
                },
                focusedEvidenceAttemptCoordinator: coordinator);

            var scheduled = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            Assert.IsType<ConductorAdvanceOutcome.Held>(scheduled.Outcome);
            Assert.Equal(0, focusedRuns);
            completionGate.RequiredHandleForTests(goal.Id.Value).CompleteForTests();
            Assert.Equal(1, focusedRuns);

            var reconciled = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(reconciled.Outcome);
            Assert.Contains("PRE_REVIEW_EVIDENCE_FAILED", escalated.Reason, StringComparison.Ordinal);
            Assert.Contains("simulated evidence fault", escalated.Reason, StringComparison.Ordinal);
            Assert.Null(reviewer.PreReviewEvidenceReceipt);
            Assert.Equal(0, dispatches);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Xunit.Fact(Timeout = 30_000)]
    [Xunit.Trait("Category", "CrossTick")]
    public void PreReviewEvidence_BuildSlotsBusy_HoldsWithoutMappingReceipt()
    {
        var root = CreateTempDirectory();
        try
        {
            var (kernel, goal) = SoftwareGoal();
            var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
            foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
            {
                PassVerification(kernel, goal, task);
            }

            var completionGate = new ConductorParallelAcceptanceAttemptCompletionGateForTests();
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(root, "pre-review-evidence-attempts"),
                isProcessAlive: _ => true,
                attemptCompletionGateForTests: completionGate,
                acquireStableSlotLease: (_, _) => throw new DotnetBuildSlotsBusyException(
                    new DotnetBuildLeaseAcquisition.SlotsBusy("pre-review-evidence", [])));
            var focusedRuns = 0;
            var dispatches = 0;
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                getPreReviewEvidenceContext: _ => FocusedPreReviewContext("slots-busy-sha"),
                runFocusedEvidence: (_, request) =>
                {
                    focusedRuns++;
                    return PassingPreReviewEvidence(request);
                },
                recordPreReviewEvidence: (goalId, taskId, receipt) =>
                    kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
                dispatchAndStart: _ =>
                {
                    dispatches++;
                    return DispatchStartOutcome.Started();
                },
                focusedEvidenceAttemptCoordinator: coordinator);

            var scheduled = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            Assert.IsType<ConductorAdvanceOutcome.Held>(scheduled.Outcome);
            Assert.Equal(0, focusedRuns);
            completionGate.RequiredHandleForTests(goal.Id.Value).CompleteForTests();

            var reconciled = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(reconciled.Outcome);
            Assert.Contains("did not run (BlockedBuildSlot)", held.Reason, StringComparison.Ordinal);
            Assert.Null(reviewer.PreReviewEvidenceReceipt);
            Assert.Equal(0, focusedRuns);
            Assert.Equal(0, dispatches);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_pre_review_stale_sha_reruns_and_same_sha_green_is_idempotent")]
    public void ConductorDriverPreReviewStaleShaRerunsAndSameShaGreenIsIdempotent()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
        {
            PassVerification(kernel, goal, task);
        }

        kernel.RecordPreReviewEvidence(
            goal.Id,
            reviewer.Id,
            GreenPreReviewReceipt(goal, "old-sha"));
        var focusedRuns = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => FocusedPreReviewContext("new-sha"),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return PassingPreReviewEvidence(request);
            },
            recordPreReviewEvidence: (goalId, taskId, receipt) =>
                kernel.RecordPreReviewEvidence(goalId, taskId, receipt));

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        Assert.Equal("new-sha", reviewer.PreReviewEvidenceReceipt?.CandidateSha);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_pre_review_red_routes_developer_with_exact_failing_test_ids")]
    public void ConductorDriverPreReviewRedRoutesDeveloperWithExactFailingTestIds()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
        {
            PassVerification(kernel, goal, task);
        }

        string? retryMessage = null;
        TaskId? retriedTaskId = null;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => new PreReviewEvidenceContext(
                "red-sha",
                [
                    "dotnet test Core.Tests --filter FullyQualifiedName~CoreFailure",
                    "dotnet test Infrastructure.Tests --filter FullyQualifiedName~InfrastructureFailure"
                ],
                "Core.Tests: FullyQualifiedName~CoreFailure; Infrastructure.Tests: FullyQualifiedName~InfrastructureFailure",
                "Cross-project change mapped to two focused checks.",
                NoApplicableTests: false,
                MappingNeedsInput: false),
            runFocusedEvidence: (_, request) => new FocusedEvidenceRunResult(
                request,
                Accepted: true,
                Passed: false,
                Summary: "one focused test failed",
                Checks:
                [
                    new AcceptanceCheckResult(
                        "pre-review focused evidence",
                        false,
                        1,
                        "[FAIL] this output tail is not an evidence contract",
                        ArtifactsPath: "C:\\receipts\\red",
                        TestResultPaths: ["C:\\receipts\\red\\result.trx"],
                        FailingTestIdentities: ["Mcg.Tests.ConductorDriverBlocksReview(value: 42)"])
                ]),
            recordPreReviewEvidence: (goalId, taskId, receipt) =>
                kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskId = taskId;
                retryMessage = message;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            });

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Contains("Mcg.Tests.ConductorDriverBlocksReview(value: 42)", retryMessage, StringComparison.Ordinal);
        Assert.Equal(developer.Id, retriedTaskId);
        Assert.Equal(WorkTaskStatus.Assigned, developer.Status);
        Assert.Equal(PreReviewEvidenceDisposition.Red, reviewer.PreReviewEvidenceReceipt?.Disposition);
        Assert.Equal(
            ["Mcg.Tests.ConductorDriverBlocksReview(value: 42)"],
            reviewer.PreReviewEvidenceReceipt?.FailingTestIdentities);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_pre_review_red_without_trx_does_not_invent_a_failing_test")]
    public void ConductorDriverPreReviewRedWithoutTrxDoesNotInventFailingTest()
    {
        var (kernel, goal) = SoftwareGoal();
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
        {
            PassVerification(kernel, goal, task);
        }

        TaskId? retriedTaskId = null;
        string? escalation = null;
        var evidenceRuns = 0;
        const string checkName = "reviewer mapped project evidence: Core.Tests";
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => FocusedPreReviewContext("plumbing-red-sha"),
            runFocusedEvidence: (_, request) =>
            {
                evidenceRuns++;
                return evidenceRuns == 1
                    ? new FocusedEvidenceRunResult(
                        request,
                        Accepted: true,
                        Passed: false,
                        Summary: "test process produced no TRX",
                        Checks:
                        [
                            new AcceptanceCheckResult(
                                checkName,
                                false,
                                1,
                                $"[FAIL] {checkName}: failed — no TRX produced")
                        ])
                    : PassingPreReviewEvidence(request);
            },
            recordPreReviewEvidence: (goalId, taskId, receipt) =>
                kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskId = taskId;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            writeEscalation: (_, _, message) => escalation = message);

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(tester.Id, retriedTaskId);
        Assert.Null(escalation);

        PassVerification(kernel, goal, tester);
        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(2, evidenceRuns);
        Assert.Null(escalation);
        Assert.Equal(PreReviewEvidenceDisposition.Green, reviewer.PreReviewEvidenceReceipt?.Disposition);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_pre_review_no_applicable_tests_is_explicit_green_path")]
    public void ConductorDriverPreReviewNoApplicableTestsIsExplicitGreenPath()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
        {
            PassVerification(kernel, goal, task);
        }

        var focusedRuns = 0;
        var dispatched = false;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => new PreReviewEvidenceContext(
                "docs-sha",
                [],
                null,
                "Documentation-only change; no build or test command is required.",
                NoApplicableTests: true,
                MappingNeedsInput: false),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return PassingPreReviewEvidence(request);
            },
            recordPreReviewEvidence: (goalId, taskId, receipt) =>
                kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
            dispatchAndStart: _ =>
            {
                dispatched = true;
                return DispatchStartOutcome.Started();
            });

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(0, focusedRuns);
        Assert.True(dispatched);
        Assert.Equal(PreReviewEvidenceDisposition.NoApplicableTests, reviewer.PreReviewEvidenceReceipt?.Disposition);
        Assert.Contains("Documentation-only", reviewer.PreReviewEvidenceReceipt?.MappingReason, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_pre_review_mapping_needs_input_blocks_paid_reviewer_start")]
    public void ConductorDriverPreReviewMappingNeedsInputBlocksPaidReviewerStart()
    {
        var (kernel, goal) = SoftwareGoal();
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
        {
            PassVerification(kernel, goal, task);
        }

        var dispatched = false;
        string? escalation = null;
        TaskId? retriedTaskId = null;
        PreReviewEvidenceReceipt? recordedReceipt = null;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => new PreReviewEvidenceContext(
                "unmapped-sha",
                ["dotnet test broad"],
                null,
                "Changed files do not map to a focused target.",
                NoApplicableTests: false,
                MappingNeedsInput: true),
            recordPreReviewEvidence: (goalId, taskId, receipt) =>
            {
                recordedReceipt = receipt;
                kernel.RecordPreReviewEvidence(goalId, taskId, receipt);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskId = taskId;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            dispatchAndStart: _ =>
            {
                dispatched = true;
                return DispatchStartOutcome.Started();
            },
            writeEscalation: (_, _, message) => escalation = message);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(dispatched);
        Assert.Null(escalation);
        Assert.Equal(tester.Id, retriedTaskId);
        Assert.Equal(PreReviewEvidenceDisposition.MappingNeedsInput, recordedReceipt?.Disposition);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_pre_review_mapper_routes_project_and_exclusion_checks_to_bounded_evidence")]
    public void ConductorDriverPreReviewMapperRoutesProjectAndExclusionChecksToBoundedEvidence()
    {
        var cases = new[]
        {
            (
                Name: "core",
                Files: new[] { "src/Mcg.AgentOrchestrator.Core/Domain/TaskSpec.cs" },
                MappingNeedsInput: false,
                NoApplicableTests: true,
                RequestFragment: (string?)null),
            (
                Name: "infrastructure",
                Files: new[] { "src/Mcg.AgentOrchestrator.Infrastructure/Workers/WorkerResultParser.cs" },
                MappingNeedsInput: false,
                NoApplicableTests: true,
                RequestFragment: (string?)null),
            (
                Name: "dashboard",
                Files: new[] { "src/Mcg.AgentOrchestrator.App/Dashboard/Rendering/DashboardRenderer.OperatorShell.cs" },
                MappingNeedsInput: false,
                NoApplicableTests: false,
                RequestFragment: "Category!=HostIntegration"),
            (
                Name: "orchestration",
                Files: new[] { "src/Mcg.AgentOrchestrator.App/Orchestration/ConductorDriver.cs" },
                MappingNeedsInput: false,
                NoApplicableTests: false,
                RequestFragment: "Infrastructure.Tests:"),
            (
                Name: "docs",
                Files: new[] { "docs/operator-runbook.md" },
                MappingNeedsInput: false,
                NoApplicableTests: true,
                RequestFragment: (string?)null),
            (
                Name: "more-than-five-source-files",
                Files: Enumerable.Range(1, 6)
                    .Select(index => $"src/Mcg.AgentOrchestrator.Core/Domain/Changed{index}.cs")
                    .ToArray(),
                MappingNeedsInput: false,
                NoApplicableTests: true,
                RequestFragment: (string?)null),
            (
                Name: "full-suite",
                Files: new[] { "Directory.Build.props" },
                MappingNeedsInput: false,
                NoApplicableTests: true,
                RequestFragment: (string?)null),
            (
                Name: "generated-unmapped",
                Files: new[] { "src/Mcg.AgentOrchestrator.Core/bin/Debug/generated.dll" },
                MappingNeedsInput: true,
                NoApplicableTests: false,
                RequestFragment: (string?)null)
        };

        foreach (var testCase in cases)
        {
            var context = ConductorDriver.BuildPreReviewEvidenceContext(
                $"{testCase.Name}-sha",
                testCase.Files);

            Assert.Equal(testCase.MappingNeedsInput, context.MappingNeedsInput);
            Assert.Equal(testCase.NoApplicableTests, context.NoApplicableTests);
            if (testCase.RequestFragment is null)
            {
                Assert.Null(context.FocusedRequest);
            }
            else
            {
                Assert.Contains(testCase.RequestFragment, context.FocusedRequest, StringComparison.Ordinal);
            }
        }
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_pre_review_respects_worker_admission_before_running_focused_evidence")]
    public void ConductorDriverPreReviewRespectsWorkerAdmissionBeforeRunningFocusedEvidence()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
        {
            PassVerification(kernel, goal, task);
        }

        var focusedRuns = 0;
        var policy = ConductorAutonomyPolicy.Conservative;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => policy.MaxConcurrentPaidWorkers,
            getPreReviewEvidenceContext: _ => FocusedPreReviewContext("admission-sha"),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                return PassingPreReviewEvidence(request);
            });

        var result = driver.AdvanceOnce(goal, policy);

        Assert.Equal(0, focusedRuns);
        Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_pre_review_mapping_gap_retries_tester_instead_of_escalating")]
    public void ConductorDriverPreReviewMappingGapRetriesTesterInsteadOfEscalating()
    {
        var (kernel, goal) = SoftwareGoal();
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
        {
            PassVerification(kernel, goal, task);
        }

        TaskId? retriedTaskId = null;
        var dispatched = false;
        var escalated = false;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => new PreReviewEvidenceContext(
                "mapping-gap-sha",
                [],
                null,
                "unmapped test impact",
                NoApplicableTests: false,
                MappingNeedsInput: true),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskId = taskId;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            dispatchAndStart: _ =>
            {
                dispatched = true;
                return DispatchStartOutcome.Started();
            },
            writeEscalation: (_, _, _) => escalated = true);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(tester.Id, retriedTaskId);
        Assert.True(dispatched);
        Assert.False(escalated);
        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_pre_review_cardinality_mismatch_is_typed_tester_fallback")]
    public void ConductorDriverPreReviewCardinalityMismatchIsTypedTesterFallback()
    {
        var (kernel, goal) = SoftwareGoal();
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
        {
            PassVerification(kernel, goal, task);
        }

        TaskId? retriedTaskId = null;
        PreReviewEvidenceReceipt? recordedReceipt = null;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => FocusedPreReviewContext("cardinality-sha"),
            runFocusedEvidence: (_, request) => new FocusedEvidenceRunResult(
                request,
                Accepted: true,
                Passed: true,
                Summary: "broker returned two checks for one planned command",
                Checks:
                [
                    new AcceptanceCheckResult("first", true, 0, null),
                    new AcceptanceCheckResult("second", true, 0, null)
                ]),
            recordPreReviewEvidence: (goalId, taskId, receipt) =>
            {
                recordedReceipt = receipt;
                kernel.RecordPreReviewEvidence(goalId, taskId, receipt);
            },
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskId = taskId;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            dispatchAndStart: _ => DispatchStartOutcome.Started());

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(tester.Id, retriedTaskId);
        Assert.All(
            recordedReceipt?.Checks ?? [],
            check => Assert.Equal("(unmapped: check/command cardinality mismatch)", check.Command));
    }

    [Xunit.Fact]
    public void PreReview_MultipleFocusedChecks_PreserveOneToOneReceiptMapping()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
        {
            PassVerification(kernel, goal, task);
        }

        string[] targets =
        [
            "Core.Tests: DispatchOutcomeClassifyTests",
            "Infrastructure.Tests: WorkerDispatchTestsWorkerResultClassification"
        ];
        var dispatches = 0;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => new PreReviewEvidenceContext(
                "multi-focused-sha",
                targets,
                string.Join("; ", targets),
                "Two focused targets map to two checks.",
                NoApplicableTests: false,
                MappingNeedsInput: false),
            runFocusedEvidence: (_, request) => new FocusedEvidenceRunResult(
                request,
                Accepted: true,
                Passed: true,
                Summary: "two focused checks passed",
                Checks:
                [
                    new AcceptanceCheckResult("core focused check", true, 0, null),
                    new AcceptanceCheckResult("infrastructure focused check", true, 0, null)
                ]),
            recordPreReviewEvidence: (goalId, taskId, receipt) =>
                kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
            dispatchAndStart: _ =>
            {
                dispatches++;
                return DispatchStartOutcome.Started();
            });

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, dispatches);
        Assert.Equal(PreReviewEvidenceDisposition.Green, reviewer.PreReviewEvidenceReceipt?.Disposition);
        Assert.Equal(targets, reviewer.PreReviewEvidenceReceipt?.Checks.Select(check => check.Command));
    }

    [Xunit.Fact]
    public void PreReview_PartialFocusedEvidence_RetriesTester()
    {
        var (kernel, goal) = SoftwareGoal();
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
        {
            PassVerification(kernel, goal, task);
        }

        string[] targets =
        [
            "Core.Tests: DispatchOutcomeClassifyTests",
            "Infrastructure.Tests: WorkerDispatchTestsWorkerResultClassification"
        ];
        TaskId? retriedTaskId = null;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => new PreReviewEvidenceContext(
                "partial-focused-sha",
                targets,
                string.Join("; ", targets),
                "Two focused targets require two checks.",
                NoApplicableTests: false,
                MappingNeedsInput: false),
            runFocusedEvidence: (_, request) => new FocusedEvidenceRunResult(
                request,
                Accepted: true,
                Passed: true,
                Summary: "only one focused check returned",
                Checks: [new AcceptanceCheckResult("partial focused check", true, 0, null)]),
            recordPreReviewEvidence: (goalId, taskId, receipt) =>
                kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskId = taskId;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            dispatchAndStart: _ => DispatchStartOutcome.Started());

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(tester.Id, retriedTaskId);
        Assert.Equal(PreReviewEvidenceDisposition.MappingNeedsInput, reviewer.PreReviewEvidenceReceipt?.Disposition);
        Assert.Equal(
            "(unmapped: check/command cardinality mismatch)",
            reviewer.PreReviewEvidenceReceipt?.Checks.Single().Command);
    }

    [Xunit.Fact]
    public void PreReview_ZeroTestApparatusFailure_RetriesTesterWithoutDeveloperFailure()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
        {
            PassVerification(kernel, goal, task);
        }

        TaskId? retriedTaskId = null;
        var check = new AcceptanceCheckResult(
            "focused selection",
            false,
            8,
            "executed 0 tests",
            FailureClassification: AcceptanceFailureClassifications.FocusedSelectionApparatusFailure,
            ExecutedTestCount: 0);
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => FocusedPreReviewContext("zero-test-sha"),
            runFocusedEvidence: (_, request) => new FocusedEvidenceRunResult(
                request,
                Accepted: true,
                Passed: false,
                Summary: "focused selection apparatus failure",
                Checks: [check],
                Arms:
                [
                    new FocusedEvidenceArmRunResult(
                        FindingEvidenceArm.Candidate,
                        "zero-test-sha",
                        FindingEvidenceArmDisposition.ApparatusFailure,
                        true,
                        false,
                        "executed=0",
                        [check])
                ],
                OutcomeReason: FindingEvidenceOutcomeReason.ApparatusFailure),
            recordPreReviewEvidence: (goalId, taskId, receipt) =>
                kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskId = taskId;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            dispatchAndStart: _ => DispatchStartOutcome.Started());

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(tester.Id, retriedTaskId);
        Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        Assert.Equal(PreReviewEvidenceDisposition.MappingNeedsInput, reviewer.PreReviewEvidenceReceipt?.Disposition);
        Assert.Empty(reviewer.PreReviewEvidenceReceipt?.FailingTestIdentities ?? []);
    }

    [Xunit.Fact]
    public void PreReview_EmptyFocusedEvidence_RetriesTester()
    {
        var (kernel, goal) = SoftwareGoal();
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id))
        {
            PassVerification(kernel, goal, task);
        }

        var target = "Infrastructure.Tests: MissingTests";
        TaskId? retriedTask = null;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => new PreReviewEvidenceContext(
                "empty-coverage-sha",
                [target],
                target,
                "Focused target should map to a lane.",
                NoApplicableTests: false,
                MappingNeedsInput: false),
            runFocusedEvidence: (_, request) => new FocusedEvidenceRunResult(
                request,
                Accepted: true,
                Passed: true,
                Summary: "focused evidence returned no checks",
                Checks: []),
            recordPreReviewEvidence: (goalId, taskId, receipt) =>
                kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTask = taskId;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            dispatchAndStart: _ => DispatchStartOutcome.Started());

        driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(tester.Id, retriedTask);
    }

    [Xunit.Fact]
    public void PreReview_NoTester_DeduplicatesEscalationPerSha()
    {
        var (kernel, goal) = ReviewGoalWithoutTester();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        PassVerification(kernel, goal, developer);
        var target = "Infrastructure.Tests: MissingTests";
        var candidateSha = "d8061f95";
        var escalations = new List<string>();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getPreReviewEvidenceContext: _ => new PreReviewEvidenceContext(
                candidateSha,
                [target],
                target,
                "Focused target should map to a lane.",
                NoApplicableTests: false,
                MappingNeedsInput: false),
            runFocusedEvidence: (_, request) => new FocusedEvidenceRunResult(
                request,
                Accepted: true,
                Passed: true,
                Summary: "focused evidence returned no checks",
                Checks: []),
            recordPreReviewEvidence: (goalId, taskId, receipt) =>
                kernel.RecordPreReviewEvidence(goalId, taskId, receipt),
            recordPreReviewMappingEscalationSuppressed: (goalId, taskId, sha, count) =>
                kernel.RecordPreReviewMappingEscalationSuppressed(goalId, taskId, sha, count),
            writeEscalation: (_, _, message) => escalations.Add(message));

        var first = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        var second = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);
        candidateSha = "c48b3be5";
        var third = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.IsType<ConductorAdvanceOutcome.Escalated>(first.Outcome);
        Assert.IsType<ConductorAdvanceOutcome.Held>(second.Outcome);
        Assert.IsType<ConductorAdvanceOutcome.Escalated>(third.Outcome);
        Assert.Equal(2, escalations.Count);
        var suppressed = Assert.Single(goal.Timeline.Where(evt =>
            evt.Kind == ProgressKind.PreReviewMappingEscalationSuppressed));
        Assert.Contains("candidate_sha=d8061f95", suppressed.Message, StringComparison.Ordinal);
        Assert.Contains("suppressed_count=1", suppressed.Message, StringComparison.Ordinal);

        const string commandMarker = "Add one with: ";
        var commandStart = escalations[0].IndexOf(commandMarker, StringComparison.Ordinal);
        Assert.True(commandStart >= 0);
        var command = escalations[0][(commandStart + commandMarker.Length)..];
        Assert.Equal(
            [
                "add-task",
                "--goal",
                goal.Id.Value[..8],
                "Tester",
                "Resolve pre-review mapping for candidate d8061f95",
                "--before-role",
                "Reviewer"
            ],
            CliArgumentParser.SplitCommand(command));
    }

    [Xunit.Fact(DisplayName = "Equal SHA identity violations salvage the prior ledger and converge normally")]
    public void IdentityViolations_EqualShaProof_SalvagePriorLedgerAndConvergeNormally()
    {
        using var repository = CreateSeededGitRepository();
        var (workingDirectory, unchangedCommit) = repository;
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        DispatchTask(kernel, goal, reviewer, "review-1", workingDirectory: workingDirectory);
        kernel.RecordDispatchBaseCommit(goal.Id, reviewer.Id, unchangedCommit);
        var opened = Enumerable.Range(0, 15)
            .Select(index => new ReviewFinding(
                $"F-{index:D2}",
                ReviewFindingState.Open,
                new ReviewFindingLocation($"src/F{index:D2}.cs", $"F{index:D2}.Run", "guard"),
                $"Blocking finding {index:D2}.",
                FindingSeverity.Blocking))
            .ToArray();
        var firstRound = string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "commands: review",
            "tests: pass - inspected evidence",
            "blockers: none",
            $"findings: {JsonSerializer.Serialize(opened)}",
            "touched_anchors: []",
            "verdict: pass",
            "END_WORKER_RESULT");
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-1",
            workingDirectory,
            0,
            firstRound,
            "",
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true));
        kernel.RetryTask(goal.Id, reviewer.Id, "fresh review");
        var touchScope = WorkerProfileDispatcher.ReadReviewRoundTouchScope(
            goal,
            reviewer,
            workingDirectory,
            unchangedCommit);
        Assert.Empty(touchScope.TouchedAnchors);
        Assert.Null(touchScope.Diagnostic);
        DispatchTask(
            kernel,
            goal,
            reviewer,
            "review-2",
            touchScope.Diagnostic,
            touchScope.TouchedAnchors,
            workingDirectory);
        kernel.RecordDispatchBaseCommit(goal.Id, reviewer.Id, unchangedCommit);
        var rejected = opened
            .Reverse()
            .Select(finding => finding.StableId == "F-00"
                ? finding with { Location = new ReviewFindingLocation("src/Moved.cs", "Moved.Run", "guard") }
                : finding)
            .Prepend(new ReviewFinding(
                "F-RECYCLED",
                ReviewFindingState.Open,
                opened[2].Location,
                "Recycled anchor.",
                FindingSeverity.Advisory))
            .ToArray();
        var rejectedRound = string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "commands: review",
            "tests: pass - inspected evidence",
            "blockers: identity contract fixture",
            $"findings: {JsonSerializer.Serialize(rejected)}",
            "touched_anchors: []",
            "verdict: needs-work",
            "END_WORKER_RESULT");
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-2",
            workingDirectory,
            1,
            rejectedRound,
            "",
            DateTimeOffset.UtcNow,
            StandardOutputPath: Path.Combine(workingDirectory, "reviewer.out.log"),
            WorkerResultPresent: true));
        var violation = Assert.IsType<ReviewFindingContractViolation>(
            reviewer.LastVerification!.ReviewFindingContractViolation);
        Assert.Equal(ReviewFindingConvergence.IdentityMovedViolationCode, violation.Code);
        Assert.Equal(2, violation.IdentityMismatches!.Count);
        var salvaged = Assert.IsAssignableFrom<IReadOnlyList<ReviewFinding>>(
            reviewer.LastVerification.MergedReviewFindings);
        Assert.Equal(opened.Length, salvaged.Count);
        Assert.All(opened, finding =>
            Assert.Contains(salvaged, candidate => candidate == finding));

        TaskId? retriedTaskId = null;
        string? retryMessage = null;
        RetryRoundKind? retryRoundKind = null;
        string? escalation = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskId = taskId;
                retryMessage = message;
                retryRoundKind = roundKind;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            writeEscalation: (_, _, message) => escalation = message);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(
            developer.Id == retriedTaskId,
            $"Expected normal Developer convergence retry but got {retriedTaskId?.Value ?? "none"}; " +
            $"outcome={result.Outcome}; escalation={escalation ?? "none"}");
        Assert.Null(retryRoundKind);
        Assert.Equal(WorkTaskStatus.Assigned, developer.Status);
        Assert.Equal(WorkTaskStatus.Assigned, tester.Status);
        Assert.Equal(WorkTaskStatus.Assigned, reviewer.Status);
        Assert.DoesNotContain("contract-repair", retryMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("suppression=missing-system-derived-round-diff-proof", retryMessage, StringComparison.Ordinal);
        Assert.Contains("open_count: 15", retryMessage, StringComparison.Ordinal);
        Assert.All(opened, finding =>
            Assert.Contains($"stable_id: {finding.StableId}", retryMessage, StringComparison.Ordinal));
        Assert.Null(escalation);
        Assert.Empty(goal.Timeline.Where(evt =>
            evt.TaskId == reviewer.Id &&
            evt.Kind == ProgressKind.TaskRetried &&
            evt.Message.StartsWith("review-finding contract-repair:", StringComparison.Ordinal)));
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == developer.Id &&
            evt.Kind == ProgressKind.TaskRetried &&
            evt.Message.Contains("auto-review-retry", StringComparison.OrdinalIgnoreCase));
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "Equal SHA untouched reopen consumes mechanical repair budget")]
    public void UntouchedReopen_ComputedEmptyProof_ConsumesRepairBudget()
    {
        using var repository = CreateSeededGitRepository();
        var (workingDirectory, unchangedCommit) = repository;
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var anchor = new ReviewFindingLocation("src/A.cs", "A.Run", "guard");
        var open = new ReviewFinding(
            "F-1",
            ReviewFindingState.Open,
            anchor,
            "Missing guard.",
            FindingSeverity.Advisory);
        DispatchTask(kernel, goal, reviewer, "review-open", workingDirectory: workingDirectory);
        kernel.RecordDispatchBaseCommit(goal.Id, reviewer.Id, unchangedCommit);
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-open",
            workingDirectory,
            0,
            ReviewerPassWithFinding(open),
            string.Empty,
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true));

        kernel.RetryTask(goal.Id, reviewer.Id, "confirm resolution");
        var resolvedTouchScope = WorkerProfileDispatcher.ReadReviewRoundTouchScope(
            goal,
            reviewer,
            workingDirectory,
            unchangedCommit);
        Assert.Empty(resolvedTouchScope.TouchedAnchors);
        DispatchTask(
            kernel,
            goal,
            reviewer,
            "review-resolved",
            resolvedTouchScope.Diagnostic,
            resolvedTouchScope.TouchedAnchors,
            workingDirectory);
        kernel.RecordDispatchBaseCommit(goal.Id, reviewer.Id, unchangedCommit);
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-resolved",
            workingDirectory,
            0,
            ReviewerPassWithFinding(open with { State = ReviewFindingState.Resolved }),
            string.Empty,
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true));
        Assert.Null(reviewer.LastVerification!.ReviewFindingContractViolation);

        kernel.RetryTask(goal.Id, reviewer.Id, "recheck unchanged commit");
        var reopenedTouchScope = WorkerProfileDispatcher.ReadReviewRoundTouchScope(
            goal,
            reviewer,
            workingDirectory,
            unchangedCommit);
        Assert.Empty(reopenedTouchScope.TouchedAnchors);
        DispatchTask(
            kernel,
            goal,
            reviewer,
            "review-reopened",
            reopenedTouchScope.Diagnostic,
            reopenedTouchScope.TouchedAnchors,
            workingDirectory);
        kernel.RecordDispatchBaseCommit(goal.Id, reviewer.Id, unchangedCommit);
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-reopened",
            workingDirectory,
            0,
            ReviewerPassWithFinding(open),
            string.Empty,
            DateTimeOffset.UtcNow,
            StandardOutputPath: Path.Combine(workingDirectory, "reviewer.out.log"),
            WorkerResultPresent: true));

        var violation = Assert.IsType<ReviewFindingContractViolation>(
            reviewer.LastVerification!.ReviewFindingContractViolation);
        Assert.Equal(ReviewFindingConvergence.UntouchedReopenViolationCode, violation.Code);

        RetryRoundKind? retryRoundKind = null;
        string? retryMessage = null;
        string? escalation = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retryRoundKind = roundKind;
                retryMessage = message;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            },
            writeEscalation: (_, _, message) => escalation = message);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(RetryRoundKind.Mechanical, retryRoundKind);
        Assert.Equal(RetryRoundKind.Mechanical, reviewer.PendingRetryRoundKind);
        Assert.Contains(ReviewFindingConvergence.UntouchedReopenViolationCode, retryMessage, StringComparison.Ordinal);
        Assert.Null(escalation);
        Assert.Single(goal.Timeline.Where(evt =>
            evt.TaskId == reviewer.Id &&
            evt.Kind == ProgressKind.TaskRetried &&
            evt.Message.StartsWith("review-finding contract-repair:", StringComparison.Ordinal)));
        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_tester_contract_violation_mechanically_retries_the_same_tester")]
    public void ConductorDriverTesterContractViolationMechanicallyRetriesSameTester()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        PassVerification(kernel, goal, developer);

        DispatchTask(kernel, goal, tester, "test-1");
        kernel.RecordDispatchExecutionResult(goal.Id, tester.Id, new TaskVerificationRecord(
            "test-1",
            "C:\\tmp",
            0,
            ReviewerPassWithAdvisory(
                "T-1",
                new ReviewFindingLocation("tests/A.cs", "A.Tests", "guard")),
            "",
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true));
        kernel.RetryTask(goal.Id, tester.Id, "recheck");
        DispatchTask(kernel, goal, tester, "test-2");
        kernel.RecordDispatchExecutionResult(goal.Id, tester.Id, new TaskVerificationRecord(
            "test-2",
            "C:\\tmp",
            0,
            ReviewerPassWithAdvisory(
                "T-1",
                new ReviewFindingLocation("tests/B.cs", "B.Tests", "guard")),
            "",
            DateTimeOffset.UtcNow,
            StandardOutputPath: "C:\\tmp\\tester.out.log",
            WorkerResultPresent: true));

        Assert.Equal(WorkTaskStatus.Failed, tester.Status);
        Assert.NotNull(tester.LastVerification!.ReviewFindingContractViolation);

        TaskId? retriedTaskId = null;
        string? retryMessage = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => DispatchStartOutcome.Started(),
            retryTaskWithRoundKind: (goalId, taskId, message, roundKind) =>
            {
                retriedTaskId = taskId;
                retryMessage = message;
                return kernel.RetryTask(goalId, taskId, message, retryRoundKind: roundKind);
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(tester.Id, retriedTaskId);
        Assert.Equal(WorkTaskStatus.Completed, developer.Status);
        Assert.Equal(WorkTaskStatus.Assigned, tester.Status);
        Assert.StartsWith("review-finding contract-repair:", retryMessage, StringComparison.Ordinal);
        Assert.Contains("Tester task", retryMessage, StringComparison.Ordinal);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_contract_repair_escalates_at_the_repair_cap")]
    public void ConductorDriverContractRepairEscalatesAtRepairCap()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        SeedReviewerIdentityViolation(kernel, goal, reviewer);
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            kernel.RetryTask(
                goal.Id,
                reviewer.Id,
                $"review-finding contract-repair: attempt {attempt}/2",
                retryRoundKind: RetryRoundKind.Mechanical);
            RecordMovedReviewerIdentityViolation(kernel, goal, reviewer);
        }

        string? escalation = null;
        var retried = false;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTaskWithRoundKind: (_, _, _, _) =>
            {
                retried = true;
                return reviewer;
            },
            writeEscalation: (_, _, message) => escalation = message);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.False(retried);
        Assert.Contains("contract-repair limit (2)", escalation, StringComparison.Ordinal);
        Assert.Contains("ERR_REVIEW_FINDING_IDENTITY_MOVED", escalation, StringComparison.Ordinal);
        Assert.Contains("canonical_open_count=1", escalation, StringComparison.Ordinal);
        Assert.Contains("verify-manual", escalation, StringComparison.Ordinal);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_unavailable_touch_proof_escalates_without_consuming_exhausted_repair_budget")]
    public void ConductorDriverUnavailableTouchProofEscalatesWithoutConsumingExhaustedRepairBudget()
    {
        const string diagnostic =
            "Round-diff touch proof unavailable because the carried finding round has no reviewed-commit baseline.";
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        SeedReviewerIdentityViolation(kernel, goal, reviewer, diagnostic);
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            kernel.RetryTask(
                goal.Id,
                reviewer.Id,
                $"review-finding contract-repair: attempt {attempt}/2",
                retryRoundKind: RetryRoundKind.Mechanical);
            RecordMovedReviewerIdentityViolation(kernel, goal, reviewer, diagnostic);
        }

        var retried = false;
        string? escalation = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTaskWithRoundKind: (_, _, _, _) =>
            {
                retried = true;
                return reviewer;
            },
            writeEscalation: (_, _, message) => escalation = message);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.False(retried);
        Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Assert.Contains("ERR_REVIEW_FINDING_IDENTITY_MOVED", escalation, StringComparison.Ordinal);
        Assert.Contains("suppression=missing-system-derived-round-diff-proof", escalation, StringComparison.Ordinal);
        Assert.Contains("mechanical repair budget was not consumed", escalation, StringComparison.Ordinal);
        Assert.Contains(diagnostic, escalation, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Legacy identical-SHA diagnostic reproduces missing-proof suppression")]
    public void IdenticalShaLegacyDiagnostic_ReproducesMissingProofSuppression()
    {
        const string unchangedCommit = "bd7854c15aa6088fee94c13b50034ecf901f7591";
        const string legacyDiagnostic =
            $"Round-diff touch proof unavailable because the carried and current reviewed commits are identical ({unchangedCommit}).";
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var anchor = new ReviewFindingLocation("src/A.cs", "A.Run", "guard");
        var open = new ReviewFinding(
            "F-1",
            ReviewFindingState.Open,
            anchor,
            "Missing guard.",
            FindingSeverity.Advisory);
        DispatchTask(kernel, goal, reviewer, "review-open");
        kernel.RecordDispatchBaseCommit(goal.Id, reviewer.Id, unchangedCommit);
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-open",
            "C:\\tmp",
            0,
            ReviewerPassWithFinding(open),
            string.Empty,
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true));

        kernel.RetryTask(goal.Id, reviewer.Id, "confirm resolution");
        DispatchTask(kernel, goal, reviewer, "review-resolved");
        kernel.RecordDispatchBaseCommit(goal.Id, reviewer.Id, unchangedCommit);
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-resolved",
            "C:\\tmp",
            0,
            ReviewerPassWithFinding(open with { State = ReviewFindingState.Resolved }),
            string.Empty,
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true));

        kernel.RetryTask(goal.Id, reviewer.Id, "recheck unchanged commit");
        DispatchTask(kernel, goal, reviewer, "review-reopened", legacyDiagnostic);
        kernel.RecordDispatchBaseCommit(goal.Id, reviewer.Id, unchangedCommit);
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-reopened",
            "C:\\tmp",
            0,
            ReviewerPassWithFinding(open),
            string.Empty,
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true));
        Assert.Equal(
            ReviewFindingConvergence.UntouchedReopenViolationCode,
            reviewer.LastVerification!.ReviewFindingContractViolation?.Code);

        var retried = false;
        string? escalation = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTaskWithRoundKind: (_, _, _, _) =>
            {
                retried = true;
                return reviewer;
            },
            writeEscalation: (_, _, message) => escalation = message);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.False(retried);
        Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Assert.Contains(ReviewFindingConvergence.UntouchedReopenViolationCode, escalation, StringComparison.Ordinal);
        Assert.Contains("suppression=missing-system-derived-round-diff-proof", escalation, StringComparison.Ordinal);
        Assert.Contains("mechanical repair budget was not consumed", escalation, StringComparison.Ordinal);
        Assert.Contains(legacyDiagnostic, escalation, StringComparison.Ordinal);
        Assert.Empty(goal.Timeline.Where(evt =>
            evt.TaskId == reviewer.Id &&
            evt.Kind == ProgressKind.TaskRetried &&
            evt.Message.StartsWith("review-finding contract-repair:", StringComparison.Ordinal)));
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_legacy_reviewer_evidence_counters_do_not_cap_structured_requests")]
    public void ConductorDriverLegacyReviewerEvidenceCountersDoNotCapStructuredRequests()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(task => task.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        PassVerification(kernel, goal, reviewer);
        kernel.RecordReviewerEvidenceRequestReceived(goal.Id, reviewer.Id, "prior evidence request 1");
        kernel.RecordReviewerEvidenceRequestReceived(goal.Id, reviewer.Id, "prior evidence request 2");
        kernel.RecordReviewerEvidenceRequestReceived(goal.Id, reviewer.Id, "prior evidence request 3");
        kernel.RetryTask(
            goal.Id,
            reviewer.Id,
            "review-finding contract-repair: prior mechanical repair",
            retryRoundKind: RetryRoundKind.Mechanical);
        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            "still missing focused evidence",
            "Infrastructure.Tests: FullyQualifiedName~ConductorDriverTests");
        var focusedRuns = 0;
        var retried = false;
        string? escalation = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, _) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult("", true, true, "structured request ran", []);
            },
            retryTask: (goalId, taskId, message) =>
            {
                retried = true;
                return kernel.RetryTask(goalId, taskId, message);
            },
            recordFindingEvidenceOutcome: (goalId, taskId, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(goalId, taskId, stableId, outcome, receipt),
            writeEscalation: (_, _, message) => escalation = message);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        Assert.True(retried);
        Assert.Null(escalation);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_second_reviewer_evidence_request_within_bound_runs")]
    public void ConductorDriverSecondReviewerEvidenceRequestWithinBoundRuns()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(t => t.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        // One prior evidence request already served this round; a legitimate second suite must
        // still run (multi-file goals commonly need receipts for more than one changed area),
        // not escalate as a "repeat".
        kernel.RecordReviewerEvidenceRequestReceived(goal.Id, reviewer.Id, "prior reviewer evidence request");
        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            "still missing focused conductor evidence",
            "Infrastructure.Tests: FullyQualifiedName~ConductorDriverTests");
        var focusedRuns = 0;
        var retried = false;
        string? escalation = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, _) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult("", true, true, "focused evidence passed", []);
            },
            retryTask: (gid, tid, msg) =>
            {
                retried = true;
                return kernel.RetryTask(gid, tid, msg);
            },
            writeEscalation: (_, _, message) => { escalation = message; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        Assert.True(retried);
        Assert.Null(escalation);
        Assert.False(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_legacy_request_count_does_not_suppress_current_structured_request")]
    public void ConductorDriverLegacyRequestCountDoesNotSuppressCurrentStructuredRequest()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(t => t.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        // Three focused evidence requests already served this round; the fourth exceeds the
        // per-round bound and must escalate normally (loop protection is preserved).
        kernel.RecordReviewerEvidenceRequestReceived(goal.Id, reviewer.Id, "prior evidence request 1");
        kernel.RecordReviewerEvidenceRequestReceived(goal.Id, reviewer.Id, "prior evidence request 2");
        kernel.RecordReviewerEvidenceRequestReceived(goal.Id, reviewer.Id, "prior evidence request 3");
        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            "still missing focused conductor evidence",
            "Infrastructure.Tests: FullyQualifiedName~ConductorDriverTests");
        var focusedRuns = 0;
        var retried = false;
        string? escalation = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, _) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult("", true, true, "current structured request ran", []);
            },
            retryTask: (gid, tid, msg) =>
            {
                retried = true;
                return kernel.RetryTask(gid, tid, msg);
            },
            writeEscalation: (_, _, message) => { escalation = message; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        Assert.True(retried);
        Assert.Null(escalation);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_recover_style_reviewer_retry_resets_evidence_round")]
    public void ConductorDriverRecoverStyleReviewerRetryResetsEvidenceRound()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(t => t.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        // Three evidence requests from a prior review round would exceed the per-round bound.
        kernel.RecordReviewerEvidenceRequestReceived(goal.Id, reviewer.Id, "stale request 1");
        kernel.RecordReviewerEvidenceRequestReceived(goal.Id, reviewer.Id, "stale request 2");
        kernel.RecordReviewerEvidenceRequestReceived(goal.Id, reviewer.Id, "stale request 3");
        FailReviewerNeedsWork(kernel, goal, reviewer, "prior round needs evidence", "Infrastructure.Tests: prior");

        // Operator recover retries the reviewer task with a NON-mechanical message, starting a fresh
        // evidence round; the three stale requests above must no longer count toward the bound.
        kernel.RetryTask(goal.Id, reviewer.Id, "operator recover reset the review round", invalidateDownstream: false);

        FailReviewerNeedsWork(kernel, goal, reviewer, "fresh round needs evidence", "Infrastructure.Tests: fresh");
        goal = kernel.GetGoal(goal.Id);
        var focusedRuns = 0;
        var retried = false;
        string? escalation = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, _) =>
            {
                focusedRuns++;
                return new FocusedEvidenceRunResult("", true, true, "focused evidence passed", []);
            },
            retryTask: (gid, tid, msg) =>
            {
                retried = true;
                return kernel.RetryTask(gid, tid, msg);
            },
            writeEscalation: (_, _, message) => { escalation = message; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        Assert.True(retried);
        Assert.Null(escalation);
        Assert.False(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_unaccepted_structured_evidence_request_records_typed_refusal")]
    public void ConductorDriverUnacceptedStructuredEvidenceRequestRecordsTypedRefusal()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(t => t.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        FailReviewerNeedsWork(kernel, goal, reviewer, "missing full test evidence", "Infrastructure.Tests: all");
        var retried = false;
        var focusedRuns = 0;
        string? escalation = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) =>
            {
                focusedRuns++;
                Assert.Equal("Infrastructure.Tests:all", request);
                return new FocusedEvidenceRunResult(
                    request,
                    Accepted: false,
                    Passed: false,
                    Summary: "unbounded evidence request rejected",
                    Checks: [],
                    Rejection: new FocusedEvidenceRejection(
                        FocusedEvidenceRejectionCode.UnsafeFilter,
                        "all",
                        "unbounded evidence request rejected"));
            },
            retryTask: (gid, tid, msg) =>
            {
                retried = true;
                return kernel.RetryTask(gid, tid, msg);
            },
            recordFindingEvidenceRequest: (gid, tid, msg) => kernel.RecordFindingEvidenceRequest(gid, tid, msg),
            recordFindingEvidenceOutcome: (gid, tid, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(gid, tid, stableId, outcome, receipt),
            writeEscalation: (_, _, message) => { escalation = message; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.Equal(1, focusedRuns);
        Assert.True(retried);
        Assert.Null(escalation);
        var outcome = reviewer.VerificationHistory.Last().MergedReviewFindings!.Single().EvidenceOutcome;
        Assert.False(outcome?.Honoured);
        Assert.Equal(FindingEvidenceNotHonouredReason.UnparseableSelection, outcome?.Reason);
        Assert.Contains("offending_filter='all'", outcome?.Detail, StringComparison.Ordinal);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == reviewer.Id &&
            evt.Kind == ProgressKind.FindingEvidenceRequestRecorded &&
            evt.Message.Contains("reason=unparseable-selection", StringComparison.Ordinal));
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_failing_structured_evidence_run_retries_with_receipt")]
    public void ConductorDriverFailingStructuredEvidenceRunRetriesWithReceipt()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(t => t.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        FailReviewerNeedsWork(
            kernel,
            goal,
            reviewer,
            "missing focused conductor evidence",
            "Infrastructure.Tests: FullyQualifiedName~ConductorDriverTests");
        var retried = false;
        string? escalation = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            getPreReviewEvidenceContext: _ => NoPreReviewContext("abc1234"),
            runFocusedEvidence: (_, request) => new FocusedEvidenceRunResult(
                request,
                Accepted: true,
                Passed: false,
                Summary: "focused evidence failed: reviewer focused evidence exit 1; receipts: C:\\tmp\\failed-trx",
                Checks:
                [
                    new AcceptanceCheckResult(
                        "reviewer focused evidence",
                        false,
                        1,
                        "Failed! - Failed: 1",
                        ArtifactsPath: "C:\\tmp\\failed-trx")
                ]),
            retryTask: (gid, tid, msg) =>
            {
                retried = true;
                return kernel.RetryTask(gid, tid, msg);
            },
            recordFindingEvidenceRun: (gid, tid, msg) => kernel.RecordFindingEvidenceRun(gid, tid, msg),
            recordFindingEvidenceOutcome: (gid, tid, stableId, outcome, receipt) =>
                kernel.RecordFindingEvidenceOutcome(gid, tid, stableId, outcome, receipt),
            writeEscalation: (_, _, message) => { escalation = message; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(retried);
        Assert.Null(escalation);
        Assert.Contains(goal.Timeline, evt =>
            evt.TaskId == reviewer.Id &&
            evt.Kind == ProgressKind.FindingEvidenceRunRecorded &&
            evt.Message.Contains("C:\\tmp\\failed-trx", StringComparison.Ordinal));
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_reviewer_needs_work_round_7_stops_and_escalates")]
    public void ConductorDriverReviewerNeedsWorkRound7StopsAndEscalates()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Tester);
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(t => t.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        for (var i = 1; i <= 6; i++)
        {
            kernel.RetryTask(goal.Id, developer.Id, $"auto-review-retry round {i}: prior reviewer finding");
            PassVerification(kernel, goal, developer);
            PassVerification(kernel, goal, tester);
        }

        FailReviewerNeedsWork(kernel, goal, reviewer, "Developer still misses the review blocker.");
        var retried = false;
        var dispatched = false;
        string? escalation = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => { dispatched = true; return DispatchStartOutcome.Started(); },
            retryTask: (gid, tid, msg) => { retried = true; return kernel.RetryTask(gid, tid, msg); },
            writeEscalation: (_, _, message) => { escalation = message; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.False(retried);
        Assert.False(dispatched);
        Assert.Contains("auto-review-retry stopped at review round 7/7", escalation);
        Assert.Contains("operator decision required", escalation);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_reviewer_false_pass_at_cap_surfaces_retained_findings")]
    public void ConductorDriverReviewerFalsePassAtCapSurfacesRetainedFindings()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(t => t.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        var finding = new ReviewFinding(
            "F-CAP-BOUNDARY",
            ReviewFindingState.Open,
            new ReviewFindingLocation("src/Guard.cs", "Guard.Run", "preemption"),
            "Preemption remains unproved.",
            FindingSeverity.Blocking,
            FindingCategory.TestCoverage);
        FailReviewerNeedsWork(kernel, goal, reviewer, finding.Description, findings: [finding]);
        for (var i = 1; i <= 6; i++)
        {
            kernel.RetryTask(goal.Id, developer.Id, $"auto-review-retry round {i}: prior reviewer finding");
            PassVerification(kernel, goal, developer);
        }

        kernel.RetryTask(goal.Id, reviewer.Id, "operator requested final review");
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "reviewer",
            "review-cap",
            "C:\\tmp",
            DateTimeOffset.UtcNow,
            BaseCommit: "4cc96e28",
            ReviewFindingTouchedAnchors: [],
            ReviewRetryCap: new ReviewRetryCapReceipt(7, 7)));
        var resolved = finding with { State = ReviewFindingState.Resolved };
        var stdout = string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "commands: review",
            "tests: pass - inspected unchanged candidate",
            "commit: none",
            "blockers: none",
            $"findings: {JsonSerializer.Serialize(new[] { resolved })}",
            "touched_anchors: []",
            "verdict: pass",
            "model_fit: fixture/model - adequate - review",
            "skills: none",
            "confidence: high",
            "END_WORKER_RESULT");
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-cap",
            "C:\\tmp",
            0,
            stdout,
            string.Empty,
            DateTimeOffset.UtcNow,
            StandardOutputPath: "C:\\tmp\\reviewer-cap.out.log",
            WorkerResultPresent: true));

        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
        Assert.Equal(
            ReviewFindingConvergence.UnprovenResolutionAtCapViolationCode,
            reviewer.LastVerification!.ReviewFindingContractViolation!.Code);
        var dispatched = false;
        string? escalation = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => { dispatched = true; return DispatchStartOutcome.Started(); },
            writeEscalation: (_, _, message) => { escalation = message; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.False(dispatched);
        Assert.Contains("stopped at review round 7/7", escalation);
        Assert.Contains("surviving_stable_ids=F-CAP-BOUNDARY", escalation);
        Assert.Contains("candidate_sha=4cc96e28", escalation);
        Assert.Contains("continue work", escalation);
        Assert.Contains("explicit override", escalation);
        Assert.Contains("split the goal", escalation);
        Assert.Contains("supersede", escalation);
        Assert.Contains("C:\\tmp\\reviewer-cap.out.log", escalation);
        Assert.False(goal.IsTerminal);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_reviewer_honest_blocked_at_cap_surfaces_operator_decision")]
    public void ConductorDriverReviewerHonestBlockedAtCapSurfacesOperatorDecision()
    {
        var (kernel, goal) = SoftwareGoal();
        var developer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Developer);
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(t => t.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        for (var i = 1; i <= 6; i++)
        {
            kernel.RetryTask(goal.Id, developer.Id, $"auto-review-retry round {i}: prior reviewer finding");
            PassVerification(kernel, goal, developer);
        }

        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord(
            "reviewer",
            "review-cap",
            "C:\\tmp",
            DateTimeOffset.UtcNow,
            BaseCommit: "candidate-cap-sha",
            ReviewRetryCap: new ReviewRetryCapReceipt(7, 7)));
        var finding = new ReviewFinding(
            "F-HONEST-CAP",
            ReviewFindingState.Open,
            new ReviewFindingLocation("src/Guard.cs", "Guard.Run", "guard"),
            "Guard is still missing.",
            FindingSeverity.Blocking,
            FindingCategory.Correctness);
        var stdout = string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "commands: review",
            "tests: pass - inspected candidate",
            "commit: none",
            "blockers: F-HONEST-CAP - Guard is still missing.",
            $"findings: {JsonSerializer.Serialize(new[] { finding })}",
            "touched_anchors: []",
            "verdict: blocked-at-cap",
            "model_fit: fixture/model - adequate - review",
            "skills: none",
            "confidence: high",
            "END_WORKER_RESULT");
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review-cap",
            "C:\\tmp",
            0,
            stdout,
            string.Empty,
            DateTimeOffset.UtcNow,
            StandardOutputPath: "C:\\tmp\\honest-cap.out.log",
            WorkerResultPresent: true,
            ReviewedCommit: "candidate-cap-sha"));
        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);

        var dispatched = false;
        string? escalation = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => { dispatched = true; return DispatchStartOutcome.Started(); },
            writeEscalation: (_, _, message) => { escalation = message; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.False(dispatched);
        Assert.Contains("blocked-at-cap", escalation);
        Assert.Contains("surviving_stable_ids=F-HONEST-CAP", escalation);
        Assert.Contains("candidate_sha=candidate-cap-sha", escalation);
        Assert.Contains("operator decision required", escalation);
        Assert.Contains("C:\\tmp\\honest-cap.out.log", escalation);
        Assert.False(goal.IsTerminal);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "Subscription_dispatch_paths_use_workspace_configured_review_stop_round")]
    public void SubscriptionDispatchPathsUseWorkspaceConfiguredReviewStopRound()
    {
        var root = CreateTempDirectory();
        SeedLocalSkillCatalog(root);
        RunGit(root, "init");
        RunGit(root, "checkout", "-b", "main");
        RunGit(root, "config", "user.email", "test@example.com");
        RunGit(root, "config", "user.name", "Test User");
        File.WriteAllText(Path.Combine(root, "README.md"), "initial");
        RunGit(root, "add", ".");
        RunGit(root, "commit", "-m", "initial");
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        Directory.CreateDirectory(workspace.OrchestratorDirectory);
        var configuredPolicy = ConductorAutonomyPolicy.Permissive with
        {
            ReviewAutoRetryStopRound = 9
        };
        File.WriteAllText(
            Path.Combine(workspace.OrchestratorDirectory, "conductor-policy.json"),
            configuredPolicy.ToJson());

        ReviewRetryCapReceipt Prepare(string path)
        {
            var kernel = new AgentOrchestratorKernel();
            var reviewer = new TaskSpec(TaskId.New(), "Review configured cap", AgentRole.Reviewer);
            var goal = kernel.CreateGoal("Use the configured review cap", [reviewer]);
            kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
                "Review configured cap",
                ["The dispatch receipt uses the configured stop round."],
                VerificationClass.TestVerifiable,
                [],
                []));
            kernel.ActivateGoal(goal.Id, DefaultAgents());
            var profiles = WorkerProfileCatalog.Default();
            if (path == "ready-batch")
            {
                var batch = GoalManagementCommandService.SubscriptionDispatchReadyBatch(
                    kernel,
                    workspace,
                    goal,
                    DefaultAgents(),
                    profiles);
                Assert.Single(batch.Dispatches);
            }
            else if (path == "profile")
            {
                GoalManagementCommandService.ProfileDispatchTask(
                    kernel,
                    workspace,
                    goal,
                    reviewer,
                    profiles.GetRequired("codex-cli"),
                    DefaultAgents());
            }
            else
            {
                GoalManagementCommandService.SubscriptionDispatchTask(
                    kernel,
                    workspace,
                    goal,
                    reviewer,
                    DefaultAgents(),
                    profiles);
                if (path == "refresh")
                {
                    GoalManagementCommandService.RefreshPreparedDispatchBeforeStart(
                        kernel,
                        workspace,
                        goal,
                        reviewer,
                        DefaultAgents(),
                        profiles);
                }
            }

            return Assert.IsType<ReviewRetryCapReceipt>(reviewer.LastDispatch!.ReviewRetryCap);
        }

        Assert.Equal(9, Prepare("subscription-task").StopRound);
        Assert.Equal(9, Prepare("ready-batch").StopRound);
        Assert.Equal(9, Prepare("profile").StopRound);
        Assert.Equal(9, Prepare("refresh").StopRound);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_reviewer_operator_evidence_blocker_escalates_without_retry")]
    public void ConductorDriverReviewerOperatorEvidenceBlockerEscalatesWithoutRetry()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(t => t.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        FailReviewerNeedsWork(kernel, goal, reviewer, "Operator receipt required for measurement mandate before acceptance.");
        var retried = false;
        string? escalation = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTask: (gid, tid, msg) => { retried = true; return kernel.RetryTask(gid, tid, msg); },
            writeEscalation: (_, _, message) => { escalation = message; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.False(retried);
        Assert.Contains("operator-owned evidence", escalation);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_reviewer_provider_failure_does_not_auto_review_retry")]
    public void ConductorDriverReviewerProviderFailureDoesNotAutoReviewRetry()
    {
        var (kernel, goal) = SoftwareGoal();
        var reviewer = goal.Tasks.Single(t => t.RequiredRole == AgentRole.Reviewer);
        foreach (var task in goal.Tasks.Where(t => t.RequiredRole != AgentRole.Reviewer))
        {
            PassVerification(kernel, goal, task);
        }

        DispatchTask(kernel, goal, reviewer, "review");
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord(
            "review",
            "C:\\tmp",
            1,
            "ERROR: provider authentication failed before WORKER_RESULT",
            "",
            DateTimeOffset.UtcNow,
            ProviderFailureKind: ProviderFailureKind.RateLimit));
        var retried = false;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTask: (gid, tid, msg) => { retried = true; return kernel.RetryTask(gid, tid, msg); });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.False(retried);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_short_silent_launch_failure_auto_recovers_without_review_round")]
    public void ConductorDriverSilentLaunchFailureAutoRecoversWithoutReviewRound()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        DispatchTask(kernel, goal, task);
        // The root exited non-zero but both redirected streams stayed empty: this is a launch failure,
        // not a worker verdict, so the conductor should re-admit it instead of escalating.
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
            new TaskVerificationRecord(
                "test.exe",
                "C:\\tmp",
                1,
                "",
                "",
                DateTimeOffset.UtcNow,
                DispatchStartedAt: DateTimeOffset.UtcNow - TimeSpan.FromSeconds(30)));
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(1, task.EmptyOutputRetryCount);
        Assert.Equal(0, task.CriterionRetryCount);

        var retried = false;
        var escalated = false;
        string? retryMessage = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTask: (gid, tid, msg) => { retried = true; retryMessage = msg; return kernel.RetryTask(gid, tid, msg); },
            writeEscalation: (_, _, _) => { escalated = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(retried);
        Assert.False(escalated);
        Assert.Contains("zero bytes on both streams with root exit 1", retryMessage!);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
        Assert.Equal(0, task.CriterionRetryCount);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_sandbox_1312_launch_failure_uses_bounded_retry_without_commit_language")]
    public void ConductorDriverSandbox1312LaunchFailureUsesBoundedRetryWithoutCommitLanguage()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        DispatchTask(kernel, goal, task);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
            new TaskVerificationRecord(
                "test.exe",
                "C:\\tmp",
                1,
                "worker output before sandbox command launch failed",
                "CreateProcessAsUserW 1312: A specified logon session does not exist.",
                DateTimeOffset.UtcNow,
                ProviderFailureKind: ProviderFailureKind.Sandbox1312));
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(1, task.EmptyOutputRetryCount);

        string? retryMessage = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTask: (gid, tid, msg) =>
            {
                retryMessage = msg;
                return kernel.RetryTask(gid, tid, msg);
            });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Contains("sandbox command-launch failure", retryMessage!, StringComparison.Ordinal);
        Assert.Contains("sandbox logon session failed", retryMessage!, StringComparison.Ordinal);
        Assert.DoesNotContain("commit", retryMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_sandbox_preflight_failure_auto_retries_on_shared_dispatch_flake_budget")]
    public void ConductorDriverSandboxPreflightFailureAutoRetriesOnSharedDispatchFlakeBudget()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        DispatchTask(kernel, goal, task);
        // The worker never launched (sandbox launch-preflight failure): an intermittent sandbox-prep hiccup
        // that a fresh dispatch usually clears, so the conductor auto-retries on the shared dispatch-flake
        // budget instead of escalating the whole goal to the operator on a single flake.
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
            new TaskVerificationRecord(
                "test.exe",
                "C:\\tmp",
                1,
                "",
                "CreateProcessAsUser failed during Low Integrity preflight",
                DateTimeOffset.UtcNow));
        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(1, task.EmptyOutputRetryCount);

        var retried = false;
        var escalated = false;
        string? retryMessage = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTask: (gid, tid, msg) => { retried = true; retryMessage = msg; return kernel.RetryTask(gid, tid, msg); },
            writeEscalation: (_, _, _) => { escalated = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(retried);
        Assert.False(escalated);
        Assert.Contains("sandbox-preflight dispatch flake", retryMessage!);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Equal(WorkTaskStatus.Assigned, task.Status);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_scripting_failure_after_launch_preflight_retries_immediately_on_criterion_budget")]
    public void ConductorDriverScriptingFailureAfterLaunchPreflightRetriesImmediatelyOnCriterionBudget()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        const string failedCommand = "powershell.exe -Command & { while ($true) { Write-Output 'broken } }";
        DispatchTask(kernel, goal, task, failedCommand);
        const string stderr =
            "{\"event\":\"sandbox-prep\",\"phase\":\"launch-preflight\",\"elapsedMs\":200}\n" +
            "src/ProviderParser.cs:77: text.Contains(\"usage limit\", StringComparison.OrdinalIgnoreCase)\n" +
            "Worker inspected the classifier and prepared a focused change.\n" +
            "powershell.exe: ParserError: The string is missing the terminator: '.";
        kernel.RecordDispatchExecutionResult(
            goal.Id,
            task.Id,
            new TaskVerificationRecord(
                failedCommand,
                "C:\\tmp",
                1,
                string.Empty,
                stderr,
                DateTimeOffset.UtcNow,
                ProviderFailureKind: ProviderFailureKind.RateLimit));

        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Null(task.SubscriptionRetryAfter);
        Assert.False(DispatchFailureClassifier.HasRecoverableSubscriptionLimitHistory(task));

        var retried = false;
        var dispatched = false;
        var retryMessage = string.Empty;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => { dispatched = true; return DispatchStartOutcome.Started(); },
            retryTask: (gid, tid, msg) =>
            {
                retried = true;
                retryMessage = msg;
                return kernel.RetryTask(gid, tid, msg);
            },
            recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(retried);
        Assert.True(dispatched);
        Assert.Equal(1, task.CriterionRetryCount);
        Assert.Equal(1, goal.AutomaticAcceptanceRetryCount);
        Assert.Contains(failedCommand, retryMessage, StringComparison.Ordinal);
        Assert.Contains("ParserError", retryMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("subscription", retryMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_scripting_failure_escalates_after_criterion_budget_exhausted")]
    public void ConductorDriverScriptingFailureEscalatesAfterCriterionBudgetExhausted()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        kernel.RecordCriterionRetryFeedback(goal.Id, task.Id, ["first bounded retry"]);
        kernel.RecordCriterionRetryFeedback(goal.Id, task.Id, ["second bounded retry"]);
        const string failedCommand = "powershell.exe -Command broken";
        DispatchTask(kernel, goal, task, failedCommand);
        kernel.RecordDispatchExecutionResult(
            goal.Id,
            task.Id,
            new TaskVerificationRecord(
                failedCommand,
                "C:\\tmp",
                1,
                string.Empty,
                "powershell.exe: ParserError: Unexpected token '}' in expression.",
                DateTimeOffset.UtcNow));

        var retried = false;
        var dispatched = false;
        var escalationMessage = string.Empty;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => { dispatched = true; return DispatchStartOutcome.Started(); },
            retryTask: (gid, tid, msg) => { retried = true; return kernel.RetryTask(gid, tid, msg); },
            writeEscalation: (_, _, message) => { escalationMessage = message; },
            recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.False(retried);
        Assert.False(dispatched);
        Assert.Contains("exhausted bounded real-failure retries (2/2)", escalationMessage, StringComparison.Ordinal);
        Assert.Contains("ParserError", escalationMessage, StringComparison.Ordinal);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_sandbox_preflight_budget_exhaustion_escalates_after_bounded_retries")]
    public void ConductorDriverSandboxPreflightBudgetExhaustionEscalatesAfterBoundedRetries()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        const string preflightStderr = "CreateProcessAsUser failed during Low Integrity preflight";
        for (var i = 0; i < 2; i++)
        {
            DispatchTask(kernel, goal, task);
            kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
                new TaskVerificationRecord("test.exe", "C:\\tmp", 1, "", preflightStderr, DateTimeOffset.UtcNow));
            if (i == 0)
            {
                kernel.RetryTask(goal.Id, task.Id, "previous preflight retry");
            }
        }
        Assert.Equal(2, task.EmptyOutputRetryCount);

        var policy = ConductorAutonomyPolicy.Permissive with
        {
            MaxEmptyOutputDispatchRetries = 2,
            MaxEmptyOutputAutoRecoverCycles = 1
        };
        var escalated = false;
        var retried = false;
        string? escalationMessage = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTask: (gid, tid, msg) => { retried = true; return kernel.RetryTask(gid, tid, msg); },
            writeEscalation: (_, _, message) => { escalated = true; escalationMessage = message; });

        // count=2, maxAttempts=2 (2*1): 2 > 2 is false, so it still auto-retries.
        var result = driver.AdvanceOnce(goal, policy);
        Assert.True(retried);
        Assert.False(escalated);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);

        // The next preflight failure pushes the count past the budget -> escalate for operator action.
        DispatchTask(kernel, goal, task);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
            new TaskVerificationRecord("test.exe", "C:\\tmp", 1, "", preflightStderr, DateTimeOffset.UtcNow));
        Assert.Equal(3, task.EmptyOutputRetryCount);

        result = driver.AdvanceOnce(goal, policy);
        Assert.True(escalated);
        Assert.Contains("exhausted sandbox-preflight dispatch recovery", escalationMessage);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_stale_dispatch_recovery_retries_without_empty_output_budget")]
    public void ConductorDriverStaleDispatchRecoveryRetriesWithoutEmptyOutputBudget()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        DispatchTask(kernel, goal, task);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
            new TaskVerificationRecord(
                "test.exe",
                "C:\\tmp",
                1,
                "",
                "Dispatch recovery policy action='mark-stale' evidence='heartbeat-absent' reason='no live process, exit-absent, stale retry budget remaining=1'.",
                DateTimeOffset.UtcNow));
        Assert.Equal(0, task.EmptyOutputRetryCount);

        var retried = false;
        var dispatched = false;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => { dispatched = true; return DispatchStartOutcome.Started(); },
            retryTask: (gid, tid, msg) => { retried = true; return kernel.RetryTask(gid, tid, msg); });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(retried);
        Assert.True(dispatched);
        Assert.Equal(0, task.EmptyOutputRetryCount);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_stale_dispatch_budget_exhausted_escalates_without_retry")]
    public void ConductorDriverStaleDispatchBudgetExhaustedEscalatesWithoutRetry()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        DispatchTask(kernel, goal, task);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
            new TaskVerificationRecord(
                "test.exe",
                "C:\\tmp",
                1,
                "",
                "Dispatch recovery policy action='budget-exhausted' evidence='heartbeat-absent' reason='no live process, exit-absent, heartbeat absent' blocker='stale-dispatch retry budget exhausted'.",
                DateTimeOffset.UtcNow));
        Assert.Equal(0, task.EmptyOutputRetryCount);

        var retried = false;
        var dispatched = false;
        string? escalationMessage = null;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            dispatchAndStart: _ => { dispatched = true; return DispatchStartOutcome.Started(); },
            retryTask: (gid, tid, msg) => { retried = true; return kernel.RetryTask(gid, tid, msg); },
            writeEscalation: (_, _, message) => { escalationMessage = message; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.False(retried);
        Assert.False(dispatched);
        Assert.Equal(0, task.EmptyOutputRetryCount);
        Assert.Contains("stale-dispatch retry budget exhausted", escalationMessage);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_nonempty_stdout_failure_resets_empty_output_count_and_escalates")]
    public void ConductorDriverNonemptyStdoutFailureResetsEmptyOutputCountAndEscalates()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        DispatchTask(kernel, goal, task);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
            new TaskVerificationRecord(
                "test.exe",
                "C:\\tmp",
                1,
                "",
                "",
                DateTimeOffset.UtcNow,
                DispatchStartedAt: DateTimeOffset.UtcNow - TimeSpan.FromSeconds(30)));
        kernel.RetryTask(goal.Id, task.Id, "retry transient empty output");
        DispatchTask(kernel, goal, task);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
            new TaskVerificationRecord("test.exe", "C:\\tmp", 1, "x", "", DateTimeOffset.UtcNow));

        Assert.Equal(0, task.EmptyOutputRetryCount);
        var retried = false;
        var escalated = false;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTask: (gid, tid, msg) => { retried = true; return kernel.RetryTask(gid, tid, msg); },
            writeEscalation: (_, _, _) => { escalated = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.False(retried);
        Assert.True(escalated);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_empty_output_budget_exhaustion_auto_recovers_before_operator_escalation")]
    public void ConductorDriverEmptyOutputBudgetExhaustionAutoRecoversBeforeOperatorEscalation()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        for (var i = 0; i < 2; i++)
        {
            DispatchTask(kernel, goal, task);
            kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
                new TaskVerificationRecord(
                    "test.exe",
                    "C:\\tmp",
                    1,
                    "",
                    "",
                    DateTimeOffset.UtcNow,
                    DispatchStartedAt: DateTimeOffset.UtcNow - TimeSpan.FromSeconds(30)));
            if (i == 0)
            {
                kernel.RetryTask(goal.Id, task.Id, "previous empty output retry");
            }
        }

        var policy = ConductorAutonomyPolicy.Permissive with
        {
            MaxEmptyOutputDispatchRetries = 2,
            MaxEmptyOutputAutoRecoverCycles = 1
        };
        var retryMessage = string.Empty;
        var escalated = false;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTask: (gid, tid, msg) => { retryMessage = msg; return kernel.RetryTask(gid, tid, msg); },
            writeEscalation: (_, _, _) => { escalated = true; });

        var result = driver.AdvanceOnce(goal, policy);

        Assert.False(escalated);
        Assert.Contains("Auto-recover+re-admit", retryMessage);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);

        DispatchTask(kernel, goal, task);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
            new TaskVerificationRecord(
                "test.exe",
                "C:\\tmp",
                1,
                "",
                "",
                DateTimeOffset.UtcNow,
                DispatchStartedAt: DateTimeOffset.UtcNow - TimeSpan.FromSeconds(30)));

        result = driver.AdvanceOnce(goal, policy);

        Assert.True(escalated);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_empty_output_backoff_uses_configured_delay")]
    public void ConductorDriverEmptyOutputBackoffUsesConfiguredDelay()
    {
        var (kernel, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        for (var i = 0; i < 2; i++)
        {
            DispatchTask(kernel, goal, task);
            kernel.RecordDispatchExecutionResult(goal.Id, task.Id,
                new TaskVerificationRecord(
                    "test.exe",
                    "C:\\tmp",
                    1,
                    "",
                    "",
                    DateTimeOffset.UtcNow,
                    DispatchStartedAt: DateTimeOffset.UtcNow - TimeSpan.FromSeconds(30)));
            if (i == 0)
            {
                kernel.RetryTask(goal.Id, task.Id, "previous empty output retry");
            }
        }

        var delays = new List<TimeSpan>();
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTask: (gid, tid, msg) => kernel.RetryTask(gid, tid, msg),
            emptyOutputBackoffDelay: delays.Add);
        var policy = ConductorAutonomyPolicy.Permissive with
        {
            EmptyOutputRetryInitialDelaySeconds = 1,
            EmptyOutputRetryBackoffMultiplier = 2,
            EmptyOutputRetryMaxDelaySeconds = 3
        };

        driver.AdvanceOnce(goal, policy);

        Assert.Single(delays);
        Assert.Equal(TimeSpan.FromSeconds(2), delays[0]);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Blocked_facts_escalates")]
    public void ConductorDriverBlockedFactsEscalates()
    {
        var (_, goal) = SimpleGoal();

        var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(IsBlocked: true));

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
        Assert.Equal(GoalLifecycleState.Blocked, ((ConductorAdvanceOutcome.Escalated)result.Outcome).State);
    }

    // ── Manual policy ─────────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_Manual_policy_escalates_at_Created")]
    public void ConductorDriverManualPolicyEscalatesAtCreated()
    {
        var (_, goal) = SimpleGoal();
        var escalated = false;

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            writeEscalation: (_, _, _) => { escalated = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Manual);

        Assert.True(escalated);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
    }

    // ── Policy-over-engine promotion gate ────────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_Conservative_policy_delegates_Behavior_risk_to_landing_engine")]
    public void ConductorDriverConservativePolicyDelegatesBehaviorRiskToLandingEngine()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var escalated = false;
        var landCalled = false;

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptance: _ => true,
            classifyRisk: _ => ChangeRiskTier.Behavior,
            land: g => { landCalled = true; return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed"); },
            writeEscalation: (_, _, _) => { escalated = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.False(escalated);
        Assert.True(landCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    // ── Dispatch-start retry on transient spawn failure ───────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_WorkspaceReady_spawn_fail_then_recorded_start_success_advances_without_escalation")]
    public void ConductorDriverWorkspaceReadySpawnFailThenRecordedStartSuccessAdvancesWithoutEscalation()
    {
        var (_, goal) = SimpleGoal();
        var dispatchStartCalls = 0;
        var recordedStartCalls = 0;
        var shutdownCalled = false;
        var escalated = false;

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => 0,
            dispatchAndStart: _ =>
            {
                dispatchStartCalls++;
                return DispatchStartOutcome.SpawnFailed("Dispatched 1 task(s) but no processes started (spawn failed)");
            },
            startRecordedDispatches: _ =>
            {
                recordedStartCalls++;
                return DispatchStartOutcome.Started();
            },
            buildServerShutdown: () => { shutdownCalled = true; },
            writeEscalation: (_, _, _) => { escalated = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(1, dispatchStartCalls);
        Assert.Equal(1, recordedStartCalls);
        Assert.True(shutdownCalled);
        Assert.False(escalated);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_recoverable_sandbox_prep_action_is_remediated_and_start_retried")]
    public void ConductorDriverRecoverableSandboxPrepActionIsRemediatedAndStartRetried()
    {
        var (_, goal) = SimpleGoal();
        var action = new WorkerSandboxPrepRecoverableAction(
            Worktree: @"C:\repo\.orchestrator-worktrees\abc12345",
            SandboxRoot: @"C:\repo\.orchestrator-worktrees\abc12345\.mcg-sandbox",
            FailedRoot: @"C:\repo\.orchestrator-worktrees\abc12345",
            Reason: "Failed to apply inheritable Low integrity label.",
            RequiresRecursiveRemediation: true);
        var dispatchStartCalls = 0;
        var recordedStartCalls = 0;
        var recoveryCalls = 0;
        var retryTaskCalls = 0;
        var escalated = false;

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => 0,
            dispatchAndStart: _ =>
            {
                dispatchStartCalls++;
                return DispatchStartOutcome.RecoverableSandboxPrep(action);
            },
            startRecordedDispatches: _ =>
            {
                recordedStartCalls++;
                return DispatchStartOutcome.Started();
            },
            retryTask: (goalId, taskId, note) =>
            {
                retryTaskCalls++;
                return goal.Tasks.Single();
            },
            recoverSandboxPrep: received =>
            {
                Assert.True(ReferenceEquals(action, received));
                recoveryCalls++;
                return true;
            },
            writeEscalation: (_, _, _) => { escalated = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(1, dispatchStartCalls);
        Assert.Equal(1, recordedStartCalls);
        Assert.Equal(1, recoveryCalls);
        Assert.Equal(0, retryTaskCalls);
        Assert.False(escalated);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_recorded_start_mixed_started_and_recovery_keeps_recovery_action")]
    public void ConductorDriverRecordedStartMixedStartedAndRecoveryKeepsRecoveryAction()
    {
        var (_, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        var action = NewSandboxRecoveryAction();
        var plan = new ProcessBatchPlan(
            goal.Id,
            goal.Objective,
            goal.Status,
            ProcessBatchActionKind.StartDispatches,
            ReadyCount: 1,
            SkippedCount: 0,
            Items: []);
        var result = new ProcessBatchExecutionResult(plan, [task], [action]);

        var outcome = ConductorDriver.ClassifyRecordedDispatchStartForConductor(result);

        Assert.Equal(DispatchStartOutcomeCategory.RecoverableSandboxPrep, outcome.Category);
        Assert.True(ReferenceEquals(action, outcome.SandboxPrepRecoveryAction));
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_subscription_start_mixed_started_and_recovery_keeps_recovery_action")]
    public void ConductorDriverSubscriptionStartMixedStartedAndRecoveryKeepsRecoveryAction()
    {
        var (_, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        var action = NewSandboxRecoveryAction();
        var plan = new ProcessBatchPlan(
            goal.Id,
            goal.Objective,
            goal.Status,
            ProcessBatchActionKind.StartDispatches,
            ReadyCount: 1,
            SkippedCount: 0,
            Items: []);
        var processResult = new ProcessBatchExecutionResult(plan, [task], [action]);
        var result = new SubscriptionStartResult(
            [new WorkerProfileDispatchResult(task, @"C:\repo\.orchestrator\prompts\task.md")],
            processResult,
            new ParallelExecutionPlan([], []),
            []);

        var outcome = ConductorDriver.ClassifySubscriptionStartForConductor(result);

        Assert.Equal(DispatchStartOutcomeCategory.RecoverableSandboxPrep, outcome.Category);
        Assert.True(ReferenceEquals(action, outcome.SandboxPrepRecoveryAction));
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_recorded_start_registration_failure_is_spawn_failure")]
    public void ConductorDriverRecordedStartRegistrationFailureIsSpawnFailure()
    {
        var (_, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        var plan = new ProcessBatchPlan(
            goal.Id,
            goal.Objective,
            goal.Status,
            ProcessBatchActionKind.StartDispatches,
            ReadyCount: 1,
            SkippedCount: 0,
            Items: []);
        const string failureReason = "worker-process-registration-failed: stage=durable-registry-write";
        var result = new ProcessBatchExecutionResult(
            plan,
            [],
            StartFailures: [new DispatchProcessStartFailure(task.Id, failureReason)]);

        var outcome = ConductorDriver.ClassifyRecordedDispatchStartForConductor(result);

        Assert.Equal(DispatchStartOutcomeCategory.SpawnFailed, outcome.Category);
        Assert.Equal(failureReason, outcome.Reason);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_subscription_start_registration_failure_is_spawn_failure")]
    public void ConductorDriverSubscriptionStartRegistrationFailureIsSpawnFailure()
    {
        var (_, goal) = SimpleGoal();
        var task = goal.Tasks.Single();
        var plan = new ProcessBatchPlan(
            goal.Id,
            goal.Objective,
            goal.Status,
            ProcessBatchActionKind.StartDispatches,
            ReadyCount: 1,
            SkippedCount: 0,
            Items: []);
        const string failureReason = "worker-process-registration-failed: stage=durable-registry-write";
        var processResult = new ProcessBatchExecutionResult(
            plan,
            [],
            StartFailures: [new DispatchProcessStartFailure(task.Id, failureReason)]);
        var result = new SubscriptionStartResult(
            [new WorkerProfileDispatchResult(task, @"C:\repo\.orchestrator\prompts\task.md")],
            processResult,
            new ParallelExecutionPlan([], []),
            []);

        var outcome = ConductorDriver.ClassifySubscriptionStartForConductor(result);

        Assert.Equal(DispatchStartOutcomeCategory.SpawnFailed, outcome.Category);
        Assert.Equal(failureReason, outcome.Reason);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_recorded_start_mixed_started_and_registration_failure_keeps_live_progress")]
    public void ConductorDriverRecordedStartMixedStartedAndRegistrationFailureKeepsLiveProgress()
    {
        var (_, goal) = SoftwareGoal();
        var startedTask = goal.Tasks[0];
        var failedTask = goal.Tasks[1];
        var plan = new ProcessBatchPlan(
            goal.Id,
            goal.Objective,
            goal.Status,
            ProcessBatchActionKind.StartDispatches,
            ReadyCount: 2,
            SkippedCount: 0,
            Items: []);
        var result = new ProcessBatchExecutionResult(
            plan,
            [startedTask],
            StartFailures: [new DispatchProcessStartFailure(failedTask.Id, "worker-process-registration-failed")]);

        var outcome = ConductorDriver.ClassifyRecordedDispatchStartForConductor(result);

        Assert.Equal(DispatchStartOutcomeCategory.Started, outcome.Category);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_subscription_start_mixed_started_and_registration_failure_keeps_live_progress")]
    public void ConductorDriverSubscriptionStartMixedStartedAndRegistrationFailureKeepsLiveProgress()
    {
        var (_, goal) = SoftwareGoal();
        var startedTask = goal.Tasks[0];
        var failedTask = goal.Tasks[1];
        var plan = new ProcessBatchPlan(
            goal.Id,
            goal.Objective,
            goal.Status,
            ProcessBatchActionKind.StartDispatches,
            ReadyCount: 2,
            SkippedCount: 0,
            Items: []);
        var processResult = new ProcessBatchExecutionResult(
            plan,
            [startedTask],
            StartFailures: [new DispatchProcessStartFailure(failedTask.Id, "worker-process-registration-failed")]);
        var result = new SubscriptionStartResult(
            [new WorkerProfileDispatchResult(startedTask, @"C:\repo\.orchestrator\prompts\task.md")],
            processResult,
            new ParallelExecutionPlan([], []),
            []);

        var outcome = ConductorDriver.ClassifySubscriptionStartForConductor(result);

        Assert.Equal(DispatchStartOutcomeCategory.Started, outcome.Category);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_failed_task_with_live_sibling_defers_failure_handling")]
    public void ConductorDriverFailedTaskWithLiveSiblingDefersFailureHandling()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(
            "Defer partial batch failure",
            [
                new TaskSpec(TaskId.New(), "Failed start", AgentRole.Developer),
                new TaskSpec(TaskId.New(), "Live sibling", AgentRole.Tester)
            ]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var failedTask = goal.Tasks[0];
        var runningTask = goal.Tasks[1];
        kernel.ReportTaskProgress(goal.Id, failedTask.Id, WorkTaskStatus.Failed, "registration failed");
        DispatchTask(kernel, goal, runningTask);
        kernel.RecordTaskProcessStarted(
            goal.Id,
            runningTask.Id,
            new TaskProcessRecord(
                12345,
                "test.exe",
                @"C:\tmp",
                @"C:\tmp\stdout",
                @"C:\tmp\stderr",
                @"C:\tmp\exit",
                StartedAt: DateTimeOffset.UtcNow,
                CompletedAt: null,
                ExitCode: null));
        var retryCount = 0;
        var shutdownCount = 0;
        var escalationCount = 0;
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            retryTask: (_, _, _) =>
            {
                retryCount++;
                return failedTask;
            },
            buildServerShutdown: () => shutdownCount++,
            writeEscalation: (_, _, _) => escalationCount++);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Equal(GoalLifecycleState.Failed, held.State);
        Assert.Contains(runningTask.Id.Value[..8], held.Reason, StringComparison.Ordinal);
        Assert.Contains("deferred", held.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, retryCount);
        Assert.Equal(0, shutdownCount);
        Assert.Equal(0, escalationCount);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_WorkspaceReady_spawn_fail_twice_escalates_after_exactly_one_retry")]
    public void ConductorDriverWorkspaceReadySpawnFailTwiceEscalatesAfterExactlyOneRetry()
    {
        var (_, goal) = SimpleGoal();
        var callCount = 0;
        var shutdownCalled = false;
        string? escalationReason = null;
        const string spawnFailReason = "Dispatched 1 task(s) but no processes started (spawn failed)";

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => 0,
            dispatchAndStart: _ =>
            {
                callCount++;
                return DispatchStartOutcome.SpawnFailed(spawnFailReason);
            },
            buildServerShutdown: () => { shutdownCalled = true; },
            writeEscalation: (_, _, reason) => { escalationReason = reason; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(2, callCount);
        Assert.True(shutdownCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
        Assert.Equal(spawnFailReason, escalationReason);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_WorkspaceReady_empty_batch_with_assigned_tasks_holds_not_escalates")]
    public void ConductorDriverWorkspaceReadyEmptyBatchWithAssignedTasksHoldsNotEscalates()
    {
        var (_, goal) = SimpleGoal();
        var callCount = 0;
        var shutdownCalled = false;
        const string emptyBatchReason = "No tasks in ready batch; goal may have no assigned or ready tasks";

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => 0,
            dispatchAndStart: _ =>
            {
                callCount++;
                return DispatchStartOutcome.EmptyBatch(emptyBatchReason);
            },
            buildServerShutdown: () => { shutdownCalled = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(1, callCount);
        Assert.False(shutdownCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Held); // assigned tasks exist → Held, not Escalated
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_WorkspaceReady_started_first_try_advances_without_retry")]
    public void ConductorDriverWorkspaceReadyStartedFirstTryAdvancesWithoutRetry()
    {
        var (_, goal) = SimpleGoal();
        var callCount = 0;
        var shutdownCalled = false;

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => 0,
            dispatchAndStart: _ =>
            {
                callCount++;
                return DispatchStartOutcome.Started();
            },
            buildServerShutdown: () => { shutdownCalled = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(1, callCount);
        Assert.False(shutdownCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Permissive_policy_allows_Broad_risk_land")]
    public void ConductorDriverPermissivePolicyAllowsBroadRiskLand()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var landCalled = false;

        // Permissive: threshold Broad → all risk tiers → Auto
        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptance: _ => true,
            classifyRisk: _ => ChangeRiskTier.Broad,
            land: g => { landCalled = true; return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed"); });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(landCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    // ── Result metadata ───────────────────────────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_result_carries_goalId_goalPrefix_and_policyName")]
    public void ConductorDriverResultCarriesGoalIdGoalPrefixAndPolicyName()
    {
        var (_, goal) = SimpleGoal();
        var driver = MakeDriver(getFacts: _ => GoalLifecycleFacts.None);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(goal.Id.Value, result.GoalId);
        Assert.Equal(goal.Id.Value[..8], result.GoalPrefix);
        Assert.Equal("Conservative", result.PolicyName);
    }

    // ── Pre-landing rebase (Defect 1 fix) ────────────────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_disjoint_advance_rebase_lands_without_escalation")]
    public void ConductorDriverVerifiedDisjointAdvanceRebaseLandsWithoutEscalation()
    {
        // Goal branch behind an advanced-but-disjoint main: rebase succeeds, landing proceeds.
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var landCalled = false;
        var escalated = false;

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptance: _ => true,
            classifyRisk: _ => ChangeRiskTier.Broad,
            rebaseOntoMain: _ => new GoalWorktreeRebaseResult(
                GoalWorktreeRebaseStatus.Rebased, "goal/test", "Rebased onto main", [], null),
            land: g => { landCalled = true; return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed"); },
            writeEscalation: (_, _, _) => { escalated = true; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(landCalled);
        Assert.False(escalated);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_Verified_rebase_conflict_escalates_with_conflict_reason")]
    public void ConductorDriverVerifiedRebaseConflictEscalatesWithConflictReason()
    {
        // Genuine overlapping-hunk conflict: rebase returns Conflict → escalate, land is never called.
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var landCalled = false;
        string? escalationReason = null;

        var driver = MakeDriver(
            getFacts: _ => GoalLifecycleFacts.None,
            runAcceptance: _ => true,
            classifyRisk: _ => ChangeRiskTier.Broad,
            rebaseOntoMain: _ => new GoalWorktreeRebaseResult(
                GoalWorktreeRebaseStatus.Conflict, "goal/test",
                "Rebase found conflicts", ["src/Foo.cs"], null),
            land: g => { landCalled = true; return new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed"); },
            writeEscalation: (_, _, reason) => { escalationReason = reason; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.False(landCalled);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
        Assert.Equal(GoalLifecycleState.Verified, ((ConductorAdvanceOutcome.Escalated)result.Outcome).State);
        Assert.True(escalationReason is not null);
        Assert.Contains("conflict", escalationReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("src/Foo.cs", escalationReason!, StringComparison.OrdinalIgnoreCase);
    }

    // ── Dispatch-start reason clarity (Defect 2 fix) ─────────────────────

    [Xunit.Fact(DisplayName = "ConductorDriver_WorkspaceReady_empty_batch_with_assigned_tasks_held_reason_includes_batch_reason")]
    public void ConductorDriverWorkspaceReadyEmptyBatchWithAssignedTasksHeldReasonIncludesBatchReason()
    {
        var (_, goal) = SimpleGoal();
        string? escalationReason = null;
        const string emptyBatchReason = "No tasks in ready batch; goal may have no assigned or ready tasks";

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => 0,
            dispatchAndStart: _ => DispatchStartOutcome.EmptyBatch(emptyBatchReason),
            writeEscalation: (_, _, reason) => { escalationReason = reason; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(result.Outcome is ConductorAdvanceOutcome.Held); // assigned tasks exist → Held, not Escalated
        Assert.Null(escalationReason); // writeEscalation not called for Held
        var heldReason = ((ConductorAdvanceOutcome.Held)result.Outcome).Reason;
        Assert.True(heldReason.Contains(goal.Id.Value, StringComparison.Ordinal));
        Assert.True(heldReason.Contains(goal.Tasks.Single().Id.Value, StringComparison.Ordinal));
        Assert.True(heldReason.Contains(emptyBatchReason, StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_empty_batch_reason_names_each_assigned_task_blocker")]
    public void ConductorDriverEmptyBatchReasonNamesEachAssignedTaskBlocker()
    {
        var developerId = TaskId.New().Value;
        var testerId = TaskId.New().Value;
        var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [
                new GoalSnapshot(
                    "goal-blocked-batch",
                    "Explain blocked batch",
                    GoalStatus.Active,
                    [
                        new TaskSnapshot(developerId, "Developer still running.", AgentRole.Developer, WorkTaskStatus.Running, null, null, null, [], null, null),
                        new TaskSnapshot(testerId, "Tester waits.", AgentRole.Tester, WorkTaskStatus.Assigned, null, null, null, [], null, null)
                    ],
                    [])
            ],
            []));
        var goal = kernel.Goals.Single();

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ => DispatchStartOutcome.EmptyBatch("No tasks in ready batch; goal may have no assigned or ready tasks"),
            evaluateReadiness: _ => new DispatchReadinessReady());

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.True(held.Reason.Contains(goal.Id.Value, StringComparison.Ordinal));
        Assert.True(held.Reason.Contains(testerId, StringComparison.Ordinal));
        Assert.True(held.Reason.Contains(developerId, StringComparison.Ordinal));
        Assert.True(held.Reason.Contains("predecessor", StringComparison.OrdinalIgnoreCase));
        Assert.True(held.Reason.Contains("Running, not Completed", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_WorkspaceReady_spawn_failure_escalation_names_spawn_reason")]
    public void ConductorDriverWorkspaceReadySpawnFailureEscalationNamesSpawnReason()
    {
        var (_, goal) = SimpleGoal();
        string? escalationReason = null;
        const string spawnFailReason = "Dispatched 1 task(s) but no processes started (spawn failed)";

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => 0,
            dispatchAndStart: _ => DispatchStartOutcome.SpawnFailed(spawnFailReason),
            writeEscalation: (_, _, reason) => { escalationReason = reason; });

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

        Assert.True(result.Outcome is ConductorAdvanceOutcome.Escalated);
        Assert.Equal(spawnFailReason, escalationReason);
        Assert.Contains("spawn", escalationReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact]
    public void EmptyBatch_CancelledPredecessor_EscalatesImmediately()
    {
        var developerId = TaskId.New().Value;
        var reviewerId = TaskId.New().Value;
        var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [
                new GoalSnapshot(
                    "goal-cancelled-predecessor",
                    "Escalate terminal task dependencies",
                    GoalStatus.Active,
                    [
                        new TaskSnapshot(developerId, "Cancelled implementation.", AgentRole.Developer, WorkTaskStatus.Cancelled, null, null, null, [], null, null),
                        new TaskSnapshot(reviewerId, "Review waits forever.", AgentRole.Reviewer, WorkTaskStatus.Assigned, null, null, null, [], null, null)
                    ],
                    [])
            ],
            []));
        var goal = kernel.Goals.Single();
        string? escalationReason = null;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ => DispatchStartOutcome.EmptyBatch("No tasks in ready batch"),
            evaluateReadiness: _ => new DispatchReadinessReady(),
            writeEscalation: (_, _, reason) => escalationReason = reason);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        var escalated = Assert.IsType<ConductorAdvanceOutcome.Escalated>(result.Outcome);
        Assert.Equal(GoalLifecycleState.WorkspaceReady, escalated.State);
        Assert.Contains(developerId, escalationReason, StringComparison.Ordinal);
        Assert.Contains("Cancelled", escalationReason, StringComparison.Ordinal);
    }
}
