using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;
public abstract class ConductorBatchLoopTests
{
    private protected readonly ITestOutputHelper _output;

    private protected ConductorBatchLoopTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private protected static IReadOnlyList<AgentDefinition> DefaultAgents() => AgentCatalog.Default().Agents;

    private protected static (AgentOrchestratorKernel Kernel, Goal Goal) SimpleGoal(string objective = "Test goal")
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), objective);
        return (kernel, goal);
    }

    private protected static void PassVerification(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task)
    {
        PassVerificationAt(kernel, goal, task, DateTimeOffset.UtcNow);
    }

    private protected static void PassVerificationAt(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task, DateTimeOffset completedAt)
    {
        var dispatch = new TaskDispatchRecord("test-worker", "test.exe", "C:\\tmp", DateTimeOffset.UtcNow);
        kernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);
        var verification = new TaskVerificationRecord("test.exe", "C:\\tmp", 0, "ok", "", completedAt);
        kernel.RecordTaskVerification(goal.Id, task.Id, verification);
    }

    private protected static string StructuredReviewerResult(ReviewFinding finding, string verdict) => string.Join(
        Environment.NewLine,
        "WORKER_RESULT:",
        "files: none",
        "commands: review",
        "tests: pass - inspected evidence",
        "blockers: exact-blocker - blocking guard remains open",
        $"findings: {JsonSerializer.Serialize(new[] { finding })}",
        "touched_anchors: []",
        $"verdict: {verdict}",
        "END_WORKER_RESULT");

    private protected static Exception SqliteBusy() =>
        new InvalidOperationException("SQLite Error 5: 'database is locked'.");

    private protected static GoalWorktreeRebaseResult DefaultRebaseSuccess() =>
        new(GoalWorktreeRebaseStatus.AlreadyFastForwardable, "goal/test", "OK", [], null);

    private protected static ConductorDriver MakeDriver(
        Func<Goal, GoalLifecycleFacts>? getFacts = null,
        Func<int>? getRunningCount = null,
        Func<Goal, string>? createWorkspace = null,
        Func<Goal, DispatchStartOutcome>? dispatchAndStart = null,
        Func<Goal, bool>? runAcceptance = null,
        Func<Goal, int?, AcceptanceVerificationSummary>? runAcceptanceWithSlot = null,
        Func<Goal, GoalWorktreeRebaseResult>? rebaseOntoMain = null,
        Func<Goal, LandingEscalationRecheckResult>? recheckPreLandingRebaseConflict = null,
        Func<Goal, LandingResult>? land = null,
        Action<Goal>? record = null,
        Func<Goal, GoalWorktreeRemoveResult>? cleanup = null,
        Action<Goal, GoalLifecycleState, string>? writeEscalation = null,
        Func<Goal, ChangeRiskTier?>? classifyRisk = null,
        Func<GoalId, TaskId, string, TaskSpec>? retryTask = null,
        Func<GoalId, TaskId, IReadOnlyList<string>, int>? recordCriterionRetryFeedback = null,
        Action<Goal, string>? recordMissingBranchRetirement = null,
        Func<Goal, IReadOnlyList<string>>? getLandingFileScopes = null,
        ConductorParallelAcceptanceAttemptCoordinator? parallelAcceptanceAttemptCoordinator = null,
        Func<Goal, int>? getAcceptanceSlotCount = null,
        Func<bool>? hasGateReadyGoal = null,
        Func<int>? getWorkerAdmissionCapacity = null,
        Func<Goal, bool>? isVerificationGateSatisfied = null,
        GateReadyCandidateProjector? gateReadyCandidateProjector = null,
        Func<
            ConductorAcceptanceCohortSelection,
            IReadOnlyList<Goal>,
            ConductorAutonomyPolicy,
            ConductorAcceptanceCohortRunResult>? runAcceptanceCohort = null,
        Func<
            ConductorMergeTrainSelection,
            IReadOnlyList<Goal>,
            ConductorAutonomyPolicy,
            ConductorMergeTrainRunResult>? runMergeTrain = null,
        Func<Goal, string, IDisposable?>? tryAcquireEvidenceMutationLease = null,
        Func<Goal, ReconcileAcceptanceLeaseState?>? getEvidenceMutationLease = null,
        Action<Goal, IReadOnlyList<string>, string?, string?, IReadOnlyList<AcceptanceCheckAttribution>?, string?>? recordAcceptanceFailure = null,
        Func<Goal, (string? BranchHeadSha, string? MainHeadSha)>? resolveAcceptanceHeads = null,
        Func<Goal, PreReviewEvidenceContext>? getPreReviewEvidenceContext = null,
        Func<DateTimeOffset>? utcNow = null,
        string? executionDirectory = null) =>
        new ConductorDriver(
            getFacts ?? (_ => GoalLifecycleFacts.None),
            getRunningCount ?? (() => 0),
            createWorkspace ?? (_ => "/tmp/workspace"),
            dispatchAndStart ?? (_ => DispatchStartOutcome.Started()),
            null,
            null,
            goal => (runAcceptance ?? (_ => true))(goal)
                ? AcceptanceVerificationSummary.PassedWithNoUnmetCriteria
                : AcceptanceVerificationSummary.Failed,
            null,
            retryTask,
            null,
            recordCriterionRetryFeedback,
            null,
            rebaseOntoMain ?? (_ => DefaultRebaseSuccess()),
            land is null
                ? ((g, _) => new LandingResult(g.Id.Value, g.Id.Value[..8], new LandingDecision.Promote(), "integration", true, "Landed"))
                : ((g, _) => land(g)),
            null,
            record ?? (_ => { }),
            cleanup ?? (_ => new GoalWorktreeRemoveResult("Workspace cleaned up.", null, [], null)),
            writeEscalation ?? ((_, _, _) => { }),
            classifyRisk ?? (_ => null),
            recordMissingBranchRetirement: recordMissingBranchRetirement,
            getLandingFileScopes: getLandingFileScopes,
            runAcceptanceVerificationWithSlot: runAcceptanceWithSlot,
            parallelAcceptanceAttemptCoordinator: parallelAcceptanceAttemptCoordinator,
            getAcceptanceSlotCount: getAcceptanceSlotCount,
            hasGateReadyGoal: hasGateReadyGoal,
            getWorkerAdmissionCapacity: getWorkerAdmissionCapacity,
            recheckPreLandingRebaseConflict: recheckPreLandingRebaseConflict,
            isVerificationGateSatisfied: isVerificationGateSatisfied,
            gateReadyCandidateProjector: gateReadyCandidateProjector,
            runAcceptanceCohort: runAcceptanceCohort,
            runMergeTrain: runMergeTrain,
            tryAcquireEvidenceMutationLease: tryAcquireEvidenceMutationLease,
            getEvidenceMutationLease: getEvidenceMutationLease,
            recordAcceptanceFailureWithAttribution: recordAcceptanceFailure,
            resolveAcceptanceHeads: resolveAcceptanceHeads,
            getPreReviewEvidenceContext: getPreReviewEvidenceContext,
            utcNow: utcNow,
            executionDirectory: executionDirectory);

    // Returns a path to a stop file that does NOT exist yet.
    private protected static string NoStopPath() =>
        Path.Combine(Path.GetTempPath(), $"conduct-stop-{Guid.NewGuid():N}.txt");

    private protected static SqliteOrchestratorStateRepository OpenStateRepository(string dbPath)
        => CreateMigratedStateRepository(dbPath);

    private protected static Goal CreateVerifiedSimpleGoal(AgentOrchestratorKernel kernel, string objective)
    {
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), objective);
        PassVerification(kernel, goal, goal.Tasks.Single());
        return goal;
    }

    private protected static (AgentOrchestratorKernel Kernel, Goal InconsistentGoal, Goal HealthyGoal)
        SeedInconsistentVerifiedGoalWithHealthyNeighbor()
    {
        var seed = new AgentOrchestratorKernel();
        var inconsistentGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            seed,
            DefaultAgents(),
            "Seed inconsistent verified goal");
        var healthyGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            seed,
            DefaultAgents(),
            "Seed healthy dispatchable neighbor");
        var snapshot = seed.ExportSnapshot();
        var blockerOutput = string.Join(
            Environment.NewLine,
            "WORKER_RESULT:",
            "files: none",
            "commands: seeded verification",
            "tests: pass - process exited successfully",
            "commit: none",
            "blockers: exact-blocker - authoritative gate remains unsatisfied",
            "model_fit: test/test - adequate - fixture - fixture",
            "skills: none",
            "confidence: high",
            "END_WORKER_RESULT");
        var blockerVerification = new TaskVerificationSnapshot(
            "seeded verification",
            "C:\\tmp",
            0,
            blockerOutput,
            string.Empty,
            DateTimeOffset.Parse("2026-08-08T02:10:00Z"),
            WorkerResultPresent: true);
        var inconsistentSnapshot = snapshot.Goals.Single(goal => goal.Id == inconsistentGoal.Id.Value);
        var inconsistentTask = inconsistentSnapshot.Tasks.Single();
        var seededKernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals
                .Select(goal => goal.Id == inconsistentGoal.Id.Value
                    ? goal with
                    {
                        Status = GoalStatus.Verified,
                        Tasks =
                        [
                            inconsistentTask with
                            {
                                RequiredRole = AgentRole.Reviewer,
                                Status = WorkTaskStatus.Completed,
                                LastVerification = blockerVerification,
                                VerificationHistory = [blockerVerification]
                            }
                        ]
                    }
                    : goal)
                .ToArray()
        });

        return (
            seededKernel,
            seededKernel.GetGoal(inconsistentGoal.Id),
            seededKernel.GetGoal(healthyGoal.Id));
    }

    // Creates a stop file and returns its path.
    private protected static string ExistingStopPath()
    {
        var path = NoStopPath();
        File.WriteAllText(path, "stop");
        return path;
    }

    private protected static string CreateSeededGitRepository()
    {
        var path = Path.Combine(Path.GetTempPath(), $"mcg-batch-loop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        RunGit(path, "init");
        RunGit(path, "checkout", "-b", "main");
        RunGit(path, "config", "user.email", "tests@example.com");
        RunGit(path, "config", "user.name", "Batch Loop Tests");
        File.WriteAllText(Path.Combine(path, "seed.txt"), "seed");
        RunGit(path, "add", "-A");
        RunGit(path, "commit", "-m", "Seed");
        return path;
    }

    private protected static bool WaitUntil(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate())
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return predicate();
    }

    private protected static string CreateTempDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private protected static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private protected static ConductorParallelAcceptanceRunResult PassingRun(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy _) =>
        ConductorParallelAcceptanceRunResult.Accepted(candidate, AcceptanceVerificationSummary.PassedWithNoUnmetCriteria);

    private protected static ConductorParallelAcceptanceRunResult PassingRun(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy _,
        DotnetBuildEnvironmentLease? stableSlotLease,
        CancellationToken cancellationToken,
        AcceptanceRunExecutionOptions executionOptions) =>
        PassingRun(candidate, _);

    private protected static ConductorParallelAcceptanceAttempt ReadAttempt(string path) =>
        JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(
            File.ReadAllText(path),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private protected static void RunGit(string workingDirectory, params string[] args)
    {
        var result = GitCli.Run(workingDirectory, args);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Error}");
        }
    }

    private protected static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private protected static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private protected static string TryReadAllTextShared(string path)
    {
        try
        {
            return File.Exists(path) ? ReadAllTextShared(path) : "<missing>";
        }
        catch (Exception ex)
        {
            return $"<{ex.GetType().Name}:{ex.Message}>";
        }
    }

    private protected static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private protected static string DescribeProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.WaitForExit(100))
            {
                return $"pid={processId} running";
            }

            return $"pid={processId} exit={process.ExitCode}";
        }
        catch (ArgumentException)
        {
            return $"pid={processId} missing";
        }
        catch (InvalidOperationException)
        {
            return $"pid={processId} unavailable";
        }
    }

    private protected static void StartProcess(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        DateTimeOffset dispatchedAt,
        string baseCommit,
        int processId = 111)
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-watch-progress-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var command = $"worker {task.RequiredRole}";
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("test-worker", command, root, dispatchedAt, BaseCommit: baseCommit));
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, new TaskProcessRecord(
            processId,
            command,
            root,
            Path.Combine(root, "out.log"),
            Path.Combine(root, "err.log"),
            Path.Combine(root, "exit.txt"),
            dispatchedAt,
            null,
            null,
            OwnedProcessIds: [processId]));
    }

    private protected static void CompleteDispatchedTask(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        DateTimeOffset completedAt,
        string resultCommit)
    {
        kernel.RecordDispatchResultCommit(goal.Id, task.Id, resultCommit);
        var process = kernel.GetTask(goal.Id, task.Id).LastProcess!;
        kernel.RecordTaskProcessRefreshed(
            goal.Id,
            task.Id,
            process with { CompletedAt = completedAt, ExitCode = 0 },
            null);
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            new TaskVerificationRecord("manual", process.WorkingDirectory, 0, "ok", "", completedAt));
    }

    private protected sealed class TestWakeSignal : IConductorWakeSignal
    {
        private readonly Func<int, bool>? _wait;
        private int _waits;

        public TestWakeSignal(Action? onFirstWait = null)
            : this(onFirstWait is null
                ? null
                : waitNumber =>
                {
                    if (waitNumber == 1)
                    {
                        onFirstWait();
                        return true;
                    }

                    return false;
                })
        {
        }

        public TestWakeSignal(Func<int, bool>? wait)
        {
            _wait = wait;
        }

        public List<TimeSpan> Timeouts { get; } = [];

        public List<IReadOnlyList<string>> TrackedExitCodePathUpdates { get; } = [];

        public int SignaledWaits { get; private set; }

        public void UpdateTrackedExitArtifacts(IReadOnlyCollection<string> exitCodePaths)
        {
            TrackedExitCodePathUpdates.Add(exitCodePaths.ToArray());
        }

        public bool Wait(TimeSpan timeout)
        {
            Timeouts.Add(timeout);
            var signaled = _wait?.Invoke(++_waits) ?? false;
            if (signaled)
            {
                SignaledWaits++;
            }

            return signaled;
        }

        public void Dispose()
        {
        }
    }

    private protected static AgentOrchestratorKernel WithGoalStatus(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        GoalStatus status)
    {
        var snapshot = kernel.ExportSnapshot();
        return AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals
                .Select(goal => goal.Id == goalId.Value ? goal with { Status = status } : goal)
                .ToArray()
        });
    }
}
