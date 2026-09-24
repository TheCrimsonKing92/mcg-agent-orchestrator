using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Xml.Linq;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    internal static IReadOnlyList<AgentDefinition> DefaultAgents() => AgentCatalog.Default().Agents;

    internal static ConductorParallelAcceptanceAttemptCoordinator SeedLiveAcceptanceAttempt(
        string root,
        Goal goal)
    {
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            Path.Combine(root, "acceptance-attempts"),
            isProcessAlive: _ => true,
            launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(4242));
        var candidate = ConductorParallelAcceptanceCandidate.Create(
            goal,
            0,
            ["src/CurrentAttempt.cs"],
            "branch-current",
            "main-current");
        var decision = coordinator.Evaluate(
            candidate,
            ConductorAutonomyPolicy.Conservative,
            (_, _, _, _, _) => throw new InvalidOperationException("Owned acceptance callback should not run inline."));

        Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, decision.Kind);
        Assert.True(coordinator.HasLiveAttempt(goal.Id.Value));
        return coordinator;
    }

    internal static (AgentOrchestratorKernel Kernel, Goal Goal) SimpleGoal(string objective = "Test goal")
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), objective);
        return (kernel, goal);
    }

    internal static (AgentOrchestratorKernel Kernel, Goal Goal) SoftwareGoal(string objective = "Review retry goal")
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateGoal(kernel, DefaultAgents(), objective);
        return (kernel, goal);
    }

    internal static (AgentOrchestratorKernel Kernel, Goal Goal) ReviewGoalWithoutTester()
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

    internal static void DispatchTask(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        string command = "test.exe",
        string? reviewFindingTouchProofDiagnostic = null,
        IReadOnlyList<ReviewFindingLocation>? reviewFindingTouchedAnchors = null,
        string workingDirectory = "C:\\tmp",
        string? baseCommit = null)
    {
        var dispatch = new TaskDispatchRecord(
            "test-worker",
            command,
            workingDirectory,
            DateTimeOffset.UtcNow,
            BaseCommit: baseCommit,
            ReviewFindingTouchedAnchors: reviewFindingTouchedAnchors,
            ReviewFindingTouchProofDiagnostic: reviewFindingTouchProofDiagnostic);
        kernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);
    }

    internal static void PassVerification(
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

    internal static void FailVerification(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task)
    {
        DispatchTask(kernel, goal, task);
        // RecordDispatchExecutionResult sets WorkTaskStatus.Failed; RecordTaskVerification does not
        var verification = new TaskVerificationRecord("test.exe", "C:\\tmp", 1, "fail", "error", DateTimeOffset.UtcNow);
        kernel.RecordDispatchExecutionResult(goal.Id, task.Id, verification);
    }

    internal static void FailReviewerNeedsWork(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec reviewer,
        string blocker,
        string? evidenceRequest = null,
        string? stdoutPath = "C:\\tmp\\reviewer.out.log",
        IReadOnlyList<ReviewFinding>? findings = null,
        string? reviewedCommit = null, FindingCategory implicitFindingCategory = FindingCategory.Unspecified)
    {
        DispatchTask(kernel, goal, reviewer, "review", baseCommit: reviewedCommit);
        var effectiveFindings = (findings ?? blocker
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select((finding, index) => new ReviewFinding(
                    $"finding-{index + 1}",
                    ReviewFindingState.Open,
                    new ReviewFindingLocation("src/Test.cs", $"Test.Run{index + 1}"),
                    finding, Category: implicitFindingCategory))
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
            WorkerResultPresent: true,
            ReviewedCommit: reviewedCommit);
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, verification);
    }

    internal static void FailStructuredReviewerRound(
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

    internal static void SeedReviewerIdentityViolation(
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

    internal static void RecordMovedReviewerIdentityViolation(
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

    internal static string ReviewerPassWithAdvisory(string stableId, ReviewFindingLocation location) =>
        ReviewerPassWithFinding(new ReviewFinding(
            stableId,
            ReviewFindingState.Open,
            location,
            "Readability suggestion.",
            FindingSeverity.Advisory));

    internal static string ReviewerPassWithFinding(ReviewFinding finding) =>
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

    internal static void FailTesterBlocker(
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

    internal static void RecordInconclusiveTester(
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

    internal static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mcg-conductor-driver-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    internal static void RunGit(string workingDirectory, params string[] args)
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

    internal sealed record SeededGitRepository(string WorkingDirectory, string Head) : IDisposable
    {
        public void Dispose()
        {
            try { Directory.Delete(WorkingDirectory, recursive: true); } catch { }
        }
    }

    internal static SeededGitRepository CreateSeededGitRepository()
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

    internal static int CountOccurrences(string value, string expected)
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

    internal static GoalLifecycleFacts ReadFactsPerGoal(OrchestratorWorkspace workspace, Goal goal)
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

    internal static ConductorDriver MakeDriver(
        Func<Goal, GoalLifecycleFacts>? getFacts = null,
        Func<int>? getRunningCount = null,
        Func<Goal, string>? createWorkspace = null,
        Func<Goal, DispatchStartOutcome>? dispatchAndStart = null,
        Func<Goal, DispatchStartOutcome>? startRecordedDispatches = null,
        Action? buildServerShutdown = null,
        Func<Goal, bool>? runAcceptance = null,
        Func<Goal, AcceptanceVerificationSummary>? runAcceptanceSummary = null, Func<Goal, int?, DotnetBuildEnvironmentLease?, CancellationToken, AcceptanceVerificationSummary>? runAcceptanceVerificationWithLease = null,
        Action<Goal, AcceptanceVerificationSummary>? runAdvisorySemanticAcceptance = null,
        Func<Goal, string, FocusedEvidenceRunResult>? runFocusedEvidence = null,
        Func<GoalId, TaskId, string, TaskSpec>? retryTask = null,
        Func<GoalId, TaskId, string, RetryRoundKind?, TaskSpec>? retryTaskWithRoundKind = null,
        Func<GoalId, TaskId, string, RetryRoundKind?, RetryCause, TaskSpec>? retryTaskWithCause = null,
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
        Action<GoalId, TaskId, string, IReadOnlyList<string>, string, AgentRole, string, string>? recordFindingEvidenceSuppressed = null,
        Func<Goal, AcceptanceGateEngineSettings>? getFindingEvidenceEngineSettings = null,
        Func<Goal, string, string, IReadOnlyList<string>>? resolveFindingEvidenceSiblingClasses = null,
        Func<Goal, bool>? isVerificationGateSatisfied = null,
        GateReadyCandidateProjector? gateReadyCandidateProjector = null,
        Func<Goal, string, IDisposable?>? tryAcquireEvidenceMutationLease = null,
        Action<Goal, IReadOnlyList<string>, string?, string?, IReadOnlyList<AcceptanceCheckAttribution>?, string?>? recordAcceptanceFailure = null,
        Func<Goal, (string? BranchHeadSha, string? MainHeadSha)>? resolveAcceptanceHeads = null,
        Func<Goal, IReadOnlyList<string>>? getLandingFileScopes = null,
        string? executionDirectory = null,
        Func<Goal, TaskId, bool>? reconcileExitedDispatch = null,
        ApparatusRedGate? apparatusRedGate = null, Action<Goal, FailedGoalRecoveryDecision>? beforeFailedGoalRecoveryEffect = null)
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
            retryTaskWithCause: retryTaskWithCause,
            buildServerShutdownTimeout: buildServerShutdownTimeout,
            writeEscalationWithResult: writeEscalationWithResult,
            getPreReviewEvidenceContext: getPreReviewEvidenceContext,
            recordPreReviewEvidence: recordPreReviewEvidence,
            recordPreReviewMappingEscalationSuppressed: recordPreReviewMappingEscalationSuppressed,
            focusedEvidenceAttemptCoordinator: focusedEvidenceAttemptCoordinator,
            recordFindingEvidenceOutcome: recordFindingEvidenceOutcome,
            recordFindingEvidenceRequest: recordFindingEvidenceRequest,
            recordFindingEvidenceRun: recordFindingEvidenceRun,
            recordFindingEvidenceSuppressed: recordFindingEvidenceSuppressed,
            getFindingEvidenceEngineSettings: getFindingEvidenceEngineSettings,
            resolveFindingEvidenceSiblingClasses: resolveFindingEvidenceSiblingClasses,
            isVerificationGateSatisfied: isVerificationGateSatisfied,
            gateReadyCandidateProjector: gateReadyCandidateProjector,
            runAcceptanceVerificationWithLease: runAcceptanceVerificationWithLease,
            tryAcquireEvidenceMutationLease: tryAcquireEvidenceMutationLease,
            recordAcceptanceFailureWithAttribution: recordAcceptanceFailure,
            resolveAcceptanceHeads: resolveAcceptanceHeads,
            getLandingFileScopes: getLandingFileScopes,
            executionDirectory: executionDirectory,
            reconcileExitedDispatch: reconcileExitedDispatch,
            apparatusRedGate: apparatusRedGate, beforeFailedGoalRecoveryEffect: beforeFailedGoalRecoveryEffect);
    }

    internal static PreReviewEvidenceContext FocusedPreReviewContext(string sha) =>
        new(
            sha,
            [
                "dotnet test --project tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --verbosity minimal --filter FullyQualifiedName~ConductorDriverTests"
            ],
            "Infrastructure.Tests: FullyQualifiedName~ConductorDriverTests",
            "Orchestration change mapped to ConductorDriverTests.",
            NoApplicableTests: false,
            MappingNeedsInput: false);

    internal static PreReviewEvidenceContext NoPreReviewContext(string sha) =>
        new(
            sha,
            [],
            null,
            "No mapped pre-review tests in this routing fixture.",
            NoApplicableTests: true,
            MappingNeedsInput: false);

    internal static ReviewFinding EvidenceFinding(string description, string id = "missing-receipts") =>
        new(
            id,
            ReviewFindingState.Open,
            new ReviewFindingLocation("tests/receipts", id),
            description,
            FindingSeverity.Blocking,
            FindingCategory.Unspecified);

    internal static ReviewFinding EvidenceFindingWithRequest(
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

    internal static FocusedEvidenceRunResult PassingPreReviewEvidence(string request) =>
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

    internal static FocusedEvidenceRunResult DualArmFindingEvidence(
        string request, FindingEvidenceArmDisposition baselineDisposition,
        string candidateSha = "candidate-sha")
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
                    candidateSha,
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

    internal static FocusedEvidenceRunResult CandidateRedFindingEvidence(
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

    internal static PreReviewEvidenceReceipt GreenPreReviewReceipt(Goal goal, string sha) =>
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

    internal sealed class FakeAcceptanceVerifier : IGoalAcceptanceVerifier
    {
        public Task<AcceptanceVerificationResult> RunOwnedAsync(
            string worktreePath,
            GoalId? goalId,
            IReadOnlyList<string>? changedFiles,
            int? stableSlotIndex,
            DotnetBuildEnvironmentLease? stableSlotLease,
            IAcceptanceAttemptExecutionOwner executionOwner) =>
            Task.FromResult(new AcceptanceVerificationResult(true, false, 0, "ok"));

        public Task<FocusedEvidenceRunResult> RunFocusedEvidenceOwnedAsync(
            string worktreePath,
            GoalId? goalId,
            string request,
            IAcceptanceFocusedVerificationOwner executionOwner,
            int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null,
            bool runBaselineArm = false) =>
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

    internal sealed class ThrowingAcceptanceVerifier(Exception exception) : IGoalAcceptanceVerifier
    {
        public Task<AcceptanceVerificationResult> RunOwnedAsync(
            string worktreePath,
            GoalId? goalId,
            IReadOnlyList<string>? changedFiles,
            int? stableSlotIndex,
            DotnetBuildEnvironmentLease? stableSlotLease,
            IAcceptanceAttemptExecutionOwner executionOwner) =>
            Task.FromException<AcceptanceVerificationResult>(exception);

        public Task<FocusedEvidenceRunResult> RunFocusedEvidenceOwnedAsync(
            string worktreePath,
            GoalId? goalId,
            string request,
            IAcceptanceFocusedVerificationOwner executionOwner,
            int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null,
            bool runBaselineArm = false) =>
            Task.FromException<FocusedEvidenceRunResult>(exception);
    }

    internal sealed class CountingModelProvider(string providerName, string text) : IModelProvider
    {
        public string ProviderName { get; } = providerName;
        public int Calls { get; private set; }

        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new ModelResponse(text, null, "stop"));
        }
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
        var (kernel, goal) = SimpleGoal();
        var dispatchStartCalls = 0;
        var recordedStartCalls = 0;
        var shutdownCalled = false;
        var escalated = false;
        var phaseTimings = new List<string>();

        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => 0,
            dispatchAndStart: _ =>
            {
                dispatchStartCalls++;
                kernel.ReplaceGoalWithSnapshot(kernel.ExportGoalSnapshot(goal.Id));
                DispatchTask(kernel, goal, goal.Tasks.Single());
                return DispatchStartOutcome.SpawnFailed("Dispatched 1 task(s) but no processes started (spawn failed)");
            },
            startRecordedDispatches: currentGoal =>
            {
                recordedStartCalls++;
                Assert.False(ReferenceEquals(goal, currentGoal));
                Assert.Equal(WorkTaskStatus.Assigned, goal.Tasks.Single().Status);
                Assert.Equal(WorkTaskStatus.Running, currentGoal.Tasks.Single().Status);
                Assert.NotNull(currentGoal.Tasks.Single().LastDispatch);
                return DispatchStartOutcome.Started(currentGoal.Tasks);
            },
            buildServerShutdown: () => { shutdownCalled = true; },
            writeEscalation: (_, _, _) => { escalated = true; });
        driver.PhaseTimingSink = phaseTimings.Add;
        driver.BeginTick(kernel, tick: 1);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(1, dispatchStartCalls);
        Assert.Equal(1, recordedStartCalls);
        Assert.True(shutdownCalled);
        Assert.False(escalated);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
        Assert.Contains(
            phaseTimings,
            line => line.Contains("phase=dispatch-prep", StringComparison.Ordinal) &&
                    line.Contains("retry=spawn-failed", StringComparison.Ordinal) &&
                    line.Contains($" task={goal.Tasks.Single().Id.Value[..8]} role={goal.Tasks.Single().RequiredRole} ", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_recoverable_sandbox_prep_action_is_remediated_and_start_retried")]
    public void ConductorDriverRecoverableSandboxPrepActionIsRemediatedAndStartRetried()
    {
        var (kernel, goal) = SimpleGoal();
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
                kernel.ReplaceGoalWithSnapshot(kernel.ExportGoalSnapshot(goal.Id));
                DispatchTask(kernel, goal, goal.Tasks.Single());
                return DispatchStartOutcome.RecoverableSandboxPrep(action);
            },
            startRecordedDispatches: currentGoal =>
            {
                recordedStartCalls++;
                Assert.False(ReferenceEquals(goal, currentGoal));
                Assert.Equal(WorkTaskStatus.Assigned, goal.Tasks.Single().Status);
                Assert.Equal(WorkTaskStatus.Running, currentGoal.Tasks.Single().Status);
                Assert.NotNull(currentGoal.Tasks.Single().LastDispatch);
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
        driver.BeginTick(kernel, tick: 1);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.Equal(1, dispatchStartCalls);
        Assert.Equal(1, recordedStartCalls);
        Assert.Equal(1, recoveryCalls);
        Assert.Equal(0, retryTaskCalls);
        Assert.False(escalated);
        Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed);
    }

    [Xunit.Fact(DisplayName = "ConductorDriver_sandbox_retry_refreshes_replaced_goal_before_readiness_decision")]
    public void ConductorDriverSandboxRetryRefreshesReplacedGoalBeforeReadinessDecision()
    {
        var (kernel, goal) = SimpleGoal();
        var action = NewSandboxRecoveryAction();
        var readinessInspected = false;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ =>
            {
                kernel.ReplaceGoalWithSnapshot(kernel.ExportGoalSnapshot(goal.Id));
                DispatchTask(kernel, goal, goal.Tasks.Single());
                return DispatchStartOutcome.RecoverableSandboxPrep(action);
            },
            startRecordedDispatches: currentGoal =>
            {
                Assert.True(ReferenceEquals(kernel.GetGoal(goal.Id), currentGoal));
                kernel.ReplaceGoalWithSnapshot(kernel.ExportGoalSnapshot(goal.Id));
                return DispatchStartOutcome.EmptyBatch("retry started no processes");
            },
            recoverSandboxPrep: _ => true,
            evaluateReadiness: currentGoal =>
            {
                readinessInspected = true;
                Assert.True(ReferenceEquals(kernel.GetGoal(goal.Id), currentGoal));
                return new DispatchReadinessDeferred(
                    DateTimeOffset.UtcNow.AddMinutes(1),
                    "provider cooldown");
            });
        driver.BeginTick(kernel, tick: 1);

        var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);

        Assert.True(readinessInspected);
        Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
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

    [Xunit.Fact(DisplayName = "ConductorDriver_recorded_start_empty_batch_prioritizes_running_task_diagnostic")]
    public void ConductorDriverRecordedStartEmptyBatchPrioritizesRunningTaskDiagnostic()
    {
        var (_, goal) = SoftwareGoal();
        var completedTask = goal.Tasks[0];
        var runningTask = goal.Tasks[1];
        var plan = new ProcessBatchPlan(
            goal.Id,
            goal.Objective,
            goal.Status,
            ProcessBatchActionKind.StartDispatches,
            ReadyCount: 0,
            SkippedCount: 2,
            Items:
            [
                new ProcessBatchPlanItem(
                    completedTask.Id,
                    completedTask.RequiredRole,
                    completedTask.Description,
                    WorkTaskStatus.Completed,
                    ProcessBatchItemStatus.Skipped,
                    "Task status is Completed; only running dispatched tasks can be started."),
                new ProcessBatchPlanItem(
                    runningTask.Id,
                    runningTask.RequiredRole,
                    runningTask.Description,
                    WorkTaskStatus.Running,
                    ProcessBatchItemStatus.Skipped,
                    "Task has no recorded dispatch.")
            ]);
        var result = new ProcessBatchExecutionResult(plan, []);

        var outcome = ConductorDriver.ClassifyRecordedDispatchStartForConductor(result);

        Assert.Equal(DispatchStartOutcomeCategory.EmptyBatch, outcome.Category);
        Assert.Equal(
            "Dispatch recorded but no process was startable: Task has no recorded dispatch.",
            outcome.Reason);
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
