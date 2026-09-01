using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;


public abstract class GoalWorktreeTestBase
{
    private static readonly Lazy<ImmutableArray<byte>> MigratedStateTemplate = new(CreateMigratedStateTemplate);
    private static readonly SeedRepositoryRootInitializer SeedRepositoryProcessRoot = new(
        InitializeSeedRepositoryProcessRoot);
    private static readonly long SeedRepositoryProcessStartTimeUtcTicks = GetCurrentProcessStartTimeUtcTicks();
    private static int seedRepositoryCounter;

    // Repository evidence does not make the Windows source-root placement load-bearing:
    // non-Windows uses Temp, and the Windows move from Temp recorded no rationale.
    // Keeping it here preserves current cleanup ownership while long-path behavior is unmeasured.
    // Cohort, terminal, and orphan worktree removal can delete this subtree with the worktree.
    // The sandbox ACL reset targets only .mcg-sandbox, not this .scratch subtree.
    // Moving to Temp would leave crash leftovers to OS policy unless cleanup ownership also moved.
    private static string SeedRepositoryBaseRootPath => OperatingSystem.IsWindows()
        ? Path.Combine(FindCurrentSourceRoot(), ".scratch", "mcg-wt")
        : Path.Combine(Path.GetTempPath(), "mcg-worktree-tests");

    private protected static string SeedRepositoryProcessRootPath =>
        TempRootJanitor.BuildOwnedRootPath(SeedRepositoryBaseRootPath, Environment.ProcessId);

    private protected static AgentDefinition EchoDeveloper() => new(
        new AgentId("echo-developer"),
        "Echo Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-4o-mini", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("local"));

    private protected static IReadOnlyList<AgentDefinition> EchoAgents() =>
    [
        EchoAgent(AgentRole.Planner),
        EchoAgent(AgentRole.Researcher),
        EchoDeveloper(),
        EchoAgent(AgentRole.Tester),
        EchoAgent(AgentRole.Reviewer)
    ];

    private protected static AgentDefinition EchoAgent(AgentRole role) => new(
        new AgentId($"echo-{role.ToString().ToLowerInvariant()}"),
        $"Echo {role}",
        role,
        new ModelProfile("OpenAI", "gpt-4o-mini", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("local"));

    private protected static WorkerProfileCatalog EchoProfiles() => new(
    [
        new WorkerProfile("local", "git add -A; if ((git status --short).Length -gt 0) { git commit -m Lifecycle-work }; Write-Output {subscriptionModelName}")
    ]);

    private protected static TimeSpan FastLifecyclePollInterval => TimeSpan.FromMilliseconds(1);

    private protected static Task SkipLifecycleSleep(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;

    private protected static Func<Goal, Task<RunGoalService.RunGoalResult>> CreateFastLifecycleRunGoal(
        AgentOrchestratorKernel kernel,
        string repo)
    {
        return goal =>
        {
            var worktreePath = GoalWorktrees.Ensure(repo, goal.Id);
            var sourcePath = Path.Combine(worktreePath, "src", $"lifecycle-{goal.Id.Value[..8]}.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            File.WriteAllText(sourcePath, "// fake lifecycle work");
            RunGit(worktreePath, "add", "-A");
            if (!string.IsNullOrWhiteSpace(RunGitOutput(worktreePath, "status", "--short")))
            {
                RunGit(worktreePath, "commit", "-m", "Lifecycle-work");
            }

            var completedTasks = new List<RunGoalService.RunGoalTaskSummary>();
            foreach (var task in goal.Tasks)
            {
                kernel.RecordTaskVerification(
                    goal.Id,
                    task.Id,
                    ManualVerificationRecorder.Create(true, "Fake lifecycle run-goal passed.", worktreePath, DateTimeOffset.UtcNow));
                completedTasks.Add(new RunGoalService.RunGoalTaskSummary(
                    TaskDisplayNumber.Resolve(goal, task.Id),
                    task.Id.Value,
                    task.Description,
                    Succeeded: true,
                    OutputTail: null));
            }

            return Task.FromResult(new RunGoalService.RunGoalResult(
                Executed: true,
                StopReason: "Goal completed.",
                BlockingAction: null,
                CompletedTasks: completedTasks,
                StopEvidence: null));
        };
    }

    private protected static InMemoryModelProviderRegistry SeedSpecRefiner(OrchestratorWorkspace workspace)
    {
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("fake-refiner", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey))
        ]));
        return new InMemoryModelProviderRegistry([
            new FakeSmokeProvider("{}", providerName: "fake-refiner")
        ]);
    }

    private static string CreateSeededGitRepository(bool renameInitialBranchToMain = false)
    {
        var container = Path.Combine(
            EnsureSeedRepositoryProcessRoot(),
            BuildSeedRepositoryContainerName(
                SeedRepositoryProcessStartTimeUtcTicks,
                Interlocked.Increment(ref seedRepositoryCounter)));
        var repo = Path.Combine(container, "repo");
        try
        {
            Directory.CreateDirectory(repo);
            RunGit(repo, "init");
            RunGit(repo, "config", "user.email", "tests@example.com");
            RunGit(repo, "config", "user.name", "Worktree Tests");
            File.AppendAllText(
                Path.Combine(repo, ".git", "info", "exclude"),
                ".orchestrator/" + Environment.NewLine);
            File.WriteAllText(Path.Combine(repo, "seed.txt"), "seed");
            RunGit(repo, "add", "-A");
            RunGit(repo, "commit", "-m", "Seed");
            if (renameInitialBranchToMain)
            {
                RunGit(repo, "branch", "-M", "main");
            }

            return repo;
        }
        catch
        {
            DeleteDirectory(repo);
            throw;
        }
    }

    private static string EnsureSeedRepositoryProcessRoot() => SeedRepositoryProcessRoot.EnsureInitialized();

    private protected static string BuildSeedRepositoryContainerName(
        long processStartTimeUtcTicks,
        int sequence) =>
        $"{processStartTimeUtcTicks.ToString("x", CultureInfo.InvariantCulture)}-{sequence.ToString("x", CultureInfo.InvariantCulture)}";

    private static long GetCurrentProcessStartTimeUtcTicks()
    {
        using var process = Process.GetCurrentProcess();
        return process.StartTime.ToUniversalTime().Ticks;
    }

    private static string InitializeSeedRepositoryProcessRoot() =>
        SeedRepositoryRootInitializer.CreateProcessRoot(
            SeedRepositoryProcessRootPath,
            TempRootJanitor.DeleteTree,
            path => Directory.CreateDirectory(path));

    private protected static string CreateSeededRepository()
    {
        var root = CreateSeededGitRepository();
        _ = CreateMigratedStateRepository(
            OrchestratorWorkspace.ForDirectory(root).SqliteStatePath);
        return root;
    }

    private protected static string CreateSeededGitRepositoryForIsolation() =>
        CreateSeededGitRepository();

    private protected static string CreateReducedAcceptanceCohortRepository(
        bool renameInitialBranchToMain = true)
    {
        var repo = CreateSeededGitRepository(renameInitialBranchToMain);
        var statePath = OrchestratorWorkspace.ForDirectory(repo).SqliteStatePath;
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        File.WriteAllBytes(statePath, MigratedStateTemplate.Value.ToArray());
        return repo;
    }

    private static ImmutableArray<byte> CreateMigratedStateTemplate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-state-template-{Guid.NewGuid():N}");
        var statePath = Path.Combine(root, "state.db");
        try
        {
            _ = CreateMigratedStateRepository(statePath);
            if (!StateDbMigrations.IsUpToDate(statePath))
            {
                throw new InvalidOperationException("The reduced goal-worktree fixture state template is not fully migrated.");
            }

            return [.. File.ReadAllBytes(statePath)];
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private protected static bool HasCleanupNeededRecord(string repo, string cleanupPath, string reason)
    {
        var statePath = Path.Combine(repo, ".orchestrator", "state.db");
        if (!File.Exists(statePath))
        {
            return false;
        }

        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = statePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        conn.Open();
        using var command = conn.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM worktree_cleanup_backoff
            WHERE path = $path AND reason = $reason;
            """;
        command.Parameters.AddWithValue("$path", NormalizePath(cleanupPath));
        command.Parameters.AddWithValue("$reason", reason);
        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    }

    private protected static bool HasAnyCleanupNeededRecord(string repo, string cleanupPath)
    {
        var statePath = Path.Combine(repo, ".orchestrator", "state.db");
        if (!File.Exists(statePath))
        {
            return false;
        }

        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = statePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        conn.Open();
        using var command = conn.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM worktree_cleanup_backoff
            WHERE path = $path;
            """;
        command.Parameters.AddWithValue("$path", NormalizePath(cleanupPath));
        return Convert.ToInt32(command.ExecuteScalar()) > 0;
    }

    private protected static int CleanupJournalSkipCount(string repo, string cleanupPath)
    {
        var statePath = Path.Combine(repo, ".orchestrator", "state.db");
        if (!File.Exists(statePath))
        {
            return 0;
        }

        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = statePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        conn.Open();
        using var command = conn.CreateCommand();
        command.CommandText = """
            SELECT skip_count
            FROM worktree_cleanup_journal
            WHERE path = $path;
            """;
        command.Parameters.AddWithValue("$path", NormalizePath(cleanupPath));
        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
    }

    private protected static Goal CreateCompletedGoal(AgentOrchestratorKernel kernel, string objective, string repo)
    {
        var goal = kernel.CreateGoal(objective, [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var task = goal.Tasks.Single();
        kernel.RecordTaskVerification(
            goal.Id,
            task.Id,
            ManualVerificationRecorder.Create(true, "Passed.", repo, DateTimeOffset.UtcNow));
        Assert.Equal(GoalStatus.Verified, goal.Status);
        return goal;
    }

    private protected static CliExecutionContext CreateAcceptanceContext(
        AgentOrchestratorKernel kernel,
        string repo,
        Goal currentGoal)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(repo);
        var fakeVerifier = FakeAcceptanceVerifier.Passed();
        return new CliExecutionContext(
            kernel,
            workspace,
            new InMemoryModelProviderRegistry([]),
            AgentCatalog.Default().Agents,
            WorkerProfileCatalog.Default(),
            currentGoal)
        {
            AcceptanceVerifier = fakeVerifier,
            RunInjectedAcceptanceVerifierInCurrentProcess = true
        };
    }

    private protected static void RecordCancelledProcess(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        TaskId taskId,
        int processId,
        string workingDirectory)
    {
        var startedAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        var artifactPrefix = Path.Combine(workingDirectory, $"cancelled-{taskId.Value[..8]}");
        var standardOutputPath = artifactPrefix + ".out.log";
        var standardErrorPath = artifactPrefix + ".err.log";
        var exitCodePath = artifactPrefix + ".exit.txt";
        kernel.RecordTaskDispatch(
            goalId,
            taskId,
            new TaskDispatchRecord("codex-cli", "codex exec prompt.md", workingDirectory, startedAt));
        kernel.RecordTaskProcessStarted(
            goalId,
            taskId,
            new TaskProcessRecord(processId, "codex exec prompt.md", workingDirectory, standardOutputPath, standardErrorPath, exitCodePath, startedAt, null, null));
        kernel.RecordTaskProcessCancelled(
            goalId,
            taskId,
            new TaskProcessRecord(processId, "codex exec prompt.md", workingDirectory, standardOutputPath, standardErrorPath, exitCodePath, startedAt, DateTimeOffset.UtcNow, null, WasCancelled: true));
    }

    private protected static Process StartLongRunningHelper()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "ping.exe" : "sleep",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("-n");
            startInfo.ArgumentList.Add("30");
            startInfo.ArgumentList.Add("127.0.0.1");
        }
        else
        {
            startInfo.ArgumentList.Add("30");
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start long-running helper process.");
    }

    private protected static (int ExitCode, string Stdout, string Stderr) RunOrchestratorSqliteTool(
        string workingDirectory,
        string? repositoryRootEnvironment,
        params string[] arguments)
    {
        var sourceRoot = InfrastructureTestSupport.FindRepositoryRoot();
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (repositoryRootEnvironment is not null)
            startInfo.Environment[OrchestratorWorkspace.RepoRootEnvironmentVariable] = repositoryRootEnvironment;
        else
            startInfo.Environment.Remove(OrchestratorWorkspace.RepoRootEnvironmentVariable);
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(Path.Combine(sourceRoot, "scripts", "OrchestratorSqliteTools"));
        startInfo.ArgumentList.Add("--");
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start OrchestratorSqliteTools.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(90)), "OrchestratorSqliteTools did not exit within 90 seconds.");
        return (process.ExitCode, stdout, stderr);
    }

    private protected static void StopProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            process.WaitForExit(5000);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private protected sealed class RecordingGoalLifecycleEventWriter(List<string> order) : IGoalLifecycleEventWriter
    {
        public void AppendTimelineEvent(ProgressEvent progressEvent) { }
        public void AppendGoalCreated(GoalId goalId, string objective) { }
        public void AppendClarificationNeeded(GoalId goalId, string clarificationId) { }
        public void AppendStaleClarificationDetected(GoalId goalId, IReadOnlyList<string> staleTopicKeys, string recoveryCommand) { }
        public void AppendTaskDispatched(GoalId goalId, TaskId taskId, AgentRole role, string workerName) { }
        public void AppendWorkerProgress(GoalId goalId, long stdoutBytes, long stderrBytes, DateTimeOffset lastProgressAt) { }

        public void AppendAcceptanceResult(GoalId goalId, bool pass, IReadOnlyList<string> failures)
        {
            if (pass)
            {
                order.Add("mark-landed");
            }
        }

        public void AppendAcceptanceCriterionWaived(GoalId goalId, string criterion, string actor, DateTimeOffset recordedAt, string reason, string capturedAcceptanceCriteriaHash) { }

        public void AppendGoalLanded(GoalId goalId, string integrationBranch, string goalBranch) { }
        public void AppendGoalLandedFromAncestry(GoalId goalId, string goalBranch, string branchTip, string mainSha) { }
        public void AppendGoalLandedFromMergeEvidence(GoalId goalId, string goalBranch, string integrateSha, string mainSha) { }

        public void AppendGoalEscalated(GoalId goalId, GoalLifecycleState state, string reason, string source) { }

        public void AppendCleanedUp(GoalId goalId) => order.Add("remove-worktree");

        public void AppendProgressiveReviewGlanceReceipt(
            GoalId goalId,
            TaskId taskId,
            string trigger,
            string inputsHash,
            string verdict,
            string note,
            int inputTokens,
            int outputTokens,
            int totalTokens,
            TimeSpan wallTime,
            string? model,
            string? profile) { }

        public void AppendProgressiveReviewGlanceSummary(
            GoalId goalId,
            int totalGlances,
            int onTrack,
            int concern,
            int fundamentalMisdirection,
            int invalid,
            int totalTokens) { }
    }

    private protected sealed class CapturingGoalWorktreeService : ICliGoalWorktreeService
    {
        public int? RemoveTimeoutMilliseconds { get; private set; }

        public Func<string, GoalId, AgentOrchestratorKernel?, int?, GoalWorktreeRemoveResult>? RemoveOverride { get; init; }
        public Func<string, GoalId, AgentOrchestratorKernel, GoalWorktreeRemoveResult>? RemoveTerminalNowOverride { get; init; }

        public string BranchName(GoalId goalId) => GoalWorktrees.BranchName(goalId);

        public string Ensure(string executionDirectory, GoalId goalId) => GoalWorktrees.Ensure(executionDirectory, goalId);

        public string? TryResolve(string executionDirectory, GoalId goalId) => GoalWorktrees.TryResolve(executionDirectory, goalId);

        public GoalWorktreeRemoveResult Remove(
            string executionDirectory,
            GoalId goalId,
            AgentOrchestratorKernel? kernel = null,
            int? gitTimeoutMilliseconds = null)
        {
            RemoveTimeoutMilliseconds = gitTimeoutMilliseconds;
            if (RemoveOverride is not null)
            {
                return RemoveOverride(executionDirectory, goalId, kernel, gitTimeoutMilliseconds);
            }

            return gitTimeoutMilliseconds is { } timeout
                ? GoalWorktrees.Remove(executionDirectory, goalId, kernel, timeout)
                : GoalWorktrees.Remove(executionDirectory, goalId, kernel);
        }

        public GoalWorktreeRemoveResult RemoveTerminalNow(
            string executionDirectory,
            GoalId goalId,
            AgentOrchestratorKernel kernel) =>
            RemoveTerminalNowOverride is not null
                ? RemoveTerminalNowOverride(executionDirectory, goalId, kernel)
                : GoalWorktrees.RemoveTerminalNow(executionDirectory, goalId, kernel);

        public bool IsGitWorkTree(string executionDirectory) => GoalWorktrees.IsGitWorkTree(executionDirectory);

        public GoalWorktreeMergeResult? TryFastForwardMerge(string executionDirectory, GoalId goalId) =>
            GoalWorktrees.TryFastForwardMerge(executionDirectory, goalId);

        public GoalWorktreeRebaseResult TryRebaseOntoMain(string executionDirectory, GoalId goalId) =>
            GoalWorktrees.TryRebaseOntoMain(executionDirectory, goalId);

        public bool NeedsRebaseOntoMain(string executionDirectory, GoalId goalId) =>
            GitCli.Run(executionDirectory, "merge-base", "--is-ancestor", "HEAD", BranchName(goalId)).ExitCode != 0;

        public bool IsWorktreeClean(string executionDirectory, GoalId goalId) =>
            GoalWorktrees.IsWorktreeClean(executionDirectory, goalId);

        public bool HasChangesAgainstMain(string executionDirectory, GoalId goalId) =>
            GoalWorktrees.HasChangesAgainstMain(executionDirectory, goalId);

        public string ResolveHead(string worktreePath)
        {
            var result = GitCli.Run(worktreePath, "rev-parse", "HEAD");
            return result.Succeeded ? result.Output.Trim() : string.Empty;
        }

        public IReadOnlyList<string> GetChangedFiles(string worktreePath) =>
            GoalAcceptanceEvidenceBundleBuilder.GetChangedFiles(worktreePath);

        public GoalAcceptanceEvidenceBundle BuildAcceptanceEvidence(
            AgentOrchestratorKernel kernel,
            Goal goal,
            string? worktreePath,
            AcceptanceVerificationResult? verification,
            bool verificationSkipped,
            string? executionDirectory = null) =>
            DefaultCliGoalWorktreeService.Instance.BuildAcceptanceEvidence(
                kernel,
                goal,
                worktreePath,
                verification,
                verificationSkipped,
                executionDirectory);
    }

    private protected sealed class FakeAcceptanceVerifier(
        AcceptanceVerificationResult? result,
        Action? onRun = null,
        Exception? exception = null) : IGoalAcceptanceVerifier
    {
        public int RunCount { get; private set; }
        public bool StableSlotLeaseObserved { get; private set; }

        public Task<AcceptanceVerificationResult> RunAsync(
            string worktreePath,
            GoalId? goalId = null,
            IReadOnlyList<string>? changedFiles = null,
            int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null,
            CancellationToken cancellationToken = default)
        {
            RunCount++;
            StableSlotLeaseObserved |= stableSlotLease is not null && stableSlotIndex is not null;
            onRun?.Invoke();
            if (exception is not null)
                throw exception;

            var configuredResult = Assert.IsType<AcceptanceVerificationResult>(result);
            return Task.FromResult(AddPolicyRequiredChecks(configuredResult, changedFiles ?? []));
        }

        public Task<FocusedEvidenceRunResult> RunFocusedEvidenceAsync(
            string worktreePath,
            GoalId? goalId,
            string request,
            int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null,
            bool runBaselineArm = false,
            CancellationToken cancellationToken = default)
        {
            if (exception is not null)
                throw exception;

            return Task.FromResult(new FocusedEvidenceRunResult(
                request,
                Accepted: true,
                Passed: true,
                Summary: "focused evidence passed",
                Checks: []));
        }

        private static AcceptanceVerificationResult AddPolicyRequiredChecks(
            AcceptanceVerificationResult result,
            IReadOnlyList<string> changedFiles)
        {
            if (!result.Passed)
                return result;

            var checks = result.Checks?.ToList() ?? [];
            var existing = checks
                .Select(check => check.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var policy = VerificationPolicyCompiler.Compile(
                AgentRole.Reviewer,
                goalObjective: string.Empty,
                taskDescription: string.Empty,
                verificationPlan: null,
                changedFiles);
            foreach (var check in policy.Checks.Where(check =>
                check.Required &&
                !check.Kind.StartsWith("manual", StringComparison.OrdinalIgnoreCase) &&
                existing.Add(check.Name)))
            {
                checks.Add(new AcceptanceCheckResult(check.Name, true, 0, null));
            }

            return result with { Checks = checks };
        }

        public static FakeAcceptanceVerifier Passed(Action? onRun = null) =>
            new(
                new AcceptanceVerificationResult(
                    Passed: true,
                    Skipped: false,
                    ExitCode: 0,
                    OutputTail: null,
                    Checks: [new AcceptanceCheckResult("fake acceptance", true, 0, null)]),
                onRun);

        public static FakeAcceptanceVerifier Failed(string outputTail, Action? onRun = null) =>
            new(
                new AcceptanceVerificationResult(
                    Passed: false,
                    Skipped: false,
                    ExitCode: 1,
                    OutputTail: outputTail,
                    Checks: [new AcceptanceCheckResult("fake acceptance", false, 1, outputTail)]),
                onRun);

        public static FakeAcceptanceVerifier Timeout(Action? onRun = null) =>
            new(
                new AcceptanceVerificationResult(
                    Passed: false,
                    Skipped: false,
                    ExitCode: -1,
                    OutputTail: "Verification command timed out after elapsed=25m budget=25m.\nCommand: dotnet test infrastructure\nstdout: C:\\temp\\acc.out\nstderr: C:\\temp\\acc.err\nLast output:\nstill running",
                    ArtifactsPath: "C:\\artifacts\\goal-acceptance",
                    Checks:
                    [
                        new AcceptanceCheckResult(
                            "acceptance-check-timeout: infrastructure-tests elapsed=25m budget=25m",
                            false,
                            -1,
                            "Verification command timed out after elapsed=25m budget=25m.\nCommand: dotnet test infrastructure\nLast output:\nstill running",
                            "C:\\artifacts\\goal-acceptance",
                            ResultSummary: "elapsed=25m budget=25m")
                    ]),
                onRun);

        public static FakeAcceptanceVerifier Throws(Exception exception, Action? onRun = null) =>
            new(null, onRun, exception);
    }

    private protected static bool BranchExists(string workingDirectory, string branch)
    {
        return RunGitExitCode(workingDirectory, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}") == 0;
    }

    private protected static void RunGit(string workingDirectory, params string[] arguments)
    {
        const int maximumAttempts = 2;
        var isCommit = arguments.Any(argument => string.Equals(argument, "commit", StringComparison.Ordinal));
        var previousHead = isCommit ? TryGetGitHead(workingDirectory) : null;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            var exitCode = RunGitExitCode(workingDirectory, arguments, out var output, out var error);
            if (exitCode == 0)
            {
                return;
            }

            if (isCommit && HasNewCommittedCleanGitHead(workingDirectory, previousHead))
            {
                return;
            }

            if (isCommit && string.IsNullOrWhiteSpace(output) && string.IsNullOrWhiteSpace(error) &&
                attempt < maximumAttempts)
            {
                continue;
            }

            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed: exit={exitCode}{Environment.NewLine}" +
                $"stdout: {output.Trim()}{Environment.NewLine}" +
                $"stderr: {error.Trim()}");
        }
    }

    private protected static (int ExitCode, string Stdout, string Stderr) RunInvokeRepoGit(
        string repositoryRoot,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = repositoryRoot
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(
            ".\\scripts\\Invoke-RepoScript.ps1 scripts\\Invoke-Git.ps1 " + string.Join(' ', arguments));

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start Invoke-RepoScript.ps1.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(60000), "Invoke-RepoScript.ps1 did not exit within 60 seconds.");
        return (process.ExitCode, output, error);
    }

    private protected static string RunGitOutput(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(60000);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");
        }

        return output.Trim();
    }

    private protected static int RunGitExitCode(string workingDirectory, params string[] arguments)
    {
        return RunGitExitCode(workingDirectory, arguments, out _, out _);
    }

    private protected static int RunGitExitCode(
        string workingDirectory,
        string[] arguments,
        out string output,
        out string error)
    {
        var result = InfrastructureTestSupport.RunGitProbe(workingDirectory, arguments);
        output = result.StandardOutput;
        error = result.StandardError;
        if (result.ExitCode is int exitCode)
        {
            return exitCode;
        }

        throw new InvalidOperationException(
            $"git {string.Join(' ', arguments)} produced no exit code: " +
            $"classification={result.Classification}; processStarted={result.ProcessStarted}; " +
            $"timedOut={result.TimedOut}; drainTimedOut={result.DrainTimedOut}; " +
            $"drainFailed={result.DrainFailed}; stdoutBytes={result.StandardOutputByteCount}; " +
            $"stderrBytes={result.StandardErrorByteCount}; stderr={result.StandardError}");
    }

    private protected static (int ExitCode, string Stdout, string Stderr) ReadSeedHeadCommitId(
        string repo)
    {
        var arguments = new[] { "rev-parse", "--verify", "HEAD^{commit}" };
        var exitCode = RunGitExitCode(repo, arguments, out var output, out var error);
        return (exitCode, output, error);
    }

    private protected static string DescribeSeedHeadState(
        string repo,
        (int ExitCode, string Stdout, string Stderr) failingHead)
    {
        var receipt = new List<string>
        {
            $"failing rev-parse --verify HEAD^{{commit}} exit={failingHead.ExitCode}",
            $"failing rev-parse stdoutLength={failingHead.Stdout.Length} stdout={FormatReceiptValue(failingHead.Stdout)}",
            $"failing rev-parse stderr={FormatReceiptValue(failingHead.Stderr)}",
        };

        try
        {
            var head = ReadSeedHeadCommitId(repo);
            receipt.Add($"subsequent rev-parse --verify HEAD^{{commit}} exit={head.ExitCode}");
            receipt.Add($"subsequent rev-parse stdoutLength={head.Stdout.Length} stdout={FormatReceiptValue(head.Stdout)}");
            receipt.Add($"subsequent rev-parse stderr={FormatReceiptValue(head.Stderr)}");
        }
        catch (Exception ex)
        {
            receipt.Add($"subsequent rev-parse exception={FormatReceiptException(ex)}");
        }

        try
        {
            var exitCode = RunGitExitCode(repo, "cat-file", "-e", "HEAD^{commit}");
            receipt.Add($"cat-file -e HEAD^{{commit}} exit={exitCode}");
        }
        catch (Exception ex)
        {
            receipt.Add($"cat-file exception={FormatReceiptException(ex)}");
        }

        try
        {
            var arguments = new[] { "status", "--porcelain=v2", "--branch" };
            var exitCode = RunGitExitCode(repo, arguments, out var output, out var error);
            receipt.Add($"status --porcelain=v2 --branch exit={exitCode}");
            receipt.Add($"status stdout={FormatReceiptValue(output)}");
            receipt.Add($"status stderr={FormatReceiptValue(error)}");
        }
        catch (Exception ex)
        {
            receipt.Add($"status exception={FormatReceiptException(ex)}");
        }

        try
        {
            var gitPath = Path.Combine(repo, ".git");
            var gitKind = Directory.Exists(gitPath)
                ? "directory"
                : File.Exists(gitPath)
                    ? "file"
                    : "missing";
            receipt.Add($".git kind={gitKind}");

            if (File.Exists(gitPath))
            {
                receipt.Add($".git contents={FormatReceiptValue(File.ReadAllText(gitPath))}");
            }

            var headPath = Path.Combine(gitPath, "HEAD");
            var headText = File.Exists(headPath) ? File.ReadAllText(headPath) : string.Empty;
            receipt.Add($".git/HEAD exists={File.Exists(headPath)} contents={FormatReceiptValue(headText)}");

            const string SymrefPrefix = "ref: ";
            if (headText.StartsWith(SymrefPrefix, StringComparison.Ordinal))
            {
                var refName = headText[SymrefPrefix.Length..].Trim();
                var refPath = Path.Combine(
                    gitPath,
                    refName.Replace('/', Path.DirectorySeparatorChar));
                receipt.Add(
                    $"HEAD symref={FormatReceiptValue(refName)} looseRefExists={File.Exists(refPath)} " +
                    $"looseRefContents={FormatReceiptValue(File.Exists(refPath) ? File.ReadAllText(refPath) : string.Empty)}");
            }

            receipt.Add($"packed-refs exists={File.Exists(Path.Combine(gitPath, "packed-refs"))}");
        }
        catch (Exception ex)
        {
            receipt.Add($"git metadata exception={FormatReceiptException(ex)}");
        }

        receipt.Add(DescribeDirectoryEntries("repo entries", repo));
        receipt.Add(DescribeDirectoryEntries(
            "refs/heads entries",
            Path.Combine(repo, ".git", "refs", "heads")));
        return string.Join(Environment.NewLine, receipt);

        static string DescribeDirectoryEntries(string label, string path)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return $"{label}=<missing>";
                }

                var entries = Directory.EnumerateFileSystemEntries(path)
                    .Select(Path.GetFileName)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray();
                return $"{label}={FormatReceiptValue(string.Join(", ", entries))}";
            }
            catch (Exception ex)
            {
                return $"{label} exception={FormatReceiptException(ex)}";
            }
        }

        static string FormatReceiptValue(string value) =>
            $"'{value.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)}'";

        static string FormatReceiptException(Exception ex) =>
            $"{ex.GetType().Name}: {FormatReceiptValue(ex.Message)}";
    }

    private protected static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    private protected static string NormalizePathSeparators(string path) =>
        path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    private protected static void DeleteDirectory(string path)
    {
        if (TryResolveOwnedSeedContainer(SeedRepositoryProcessRootPath, path, out var container))
        {
            _ = TempRootJanitor.DeleteTree(container);
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; temp directories are pruned by the OS.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private protected static bool TryResolveOwnedSeedContainer(
        string processRoot,
        string path,
        out string container)
    {
        container = string.Empty;
        var normalizedPath = NormalizePath(path);
        if (!string.Equals(Path.GetFileName(normalizedPath), "repo", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var candidate = Path.GetDirectoryName(normalizedPath);
        var candidateParent = candidate is null ? null : Path.GetDirectoryName(candidate);
        if (candidateParent is null ||
            !string.Equals(
                NormalizePath(candidateParent),
                NormalizePath(processRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        container = candidate!;
        return true;
    }

    private protected static string FindCurrentSourceRoot([CallerFilePath] string sourceFilePath = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(sourceFilePath))
            ?? throw new DirectoryNotFoundException($"Could not resolve source directory from '{sourceFilePath}'."));
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "mcg-orchestrator.cmd")) &&
                File.Exists(Path.Combine(directory.FullName, "scripts", "Start-OrchestratorCommand.ps1")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate launcher source files from source file path '{sourceFilePath}'.");
    }

    private protected sealed class RecordingSandboxAclHelper : ISandboxAclHelper
    {
        public List<string> ResetPaths { get; } = [];
        public List<int> TimeoutMilliseconds { get; } = [];

        public void ResetSandboxAcl(string worktreePath, int timeoutMilliseconds)
        {
            ResetPaths.Add(worktreePath);
            TimeoutMilliseconds.Add(timeoutMilliseconds);
        }
    }

    private protected sealed class TimeoutSandboxAclHelper(RecordingSandboxAclHelper inner, Action afterReset) : ISandboxAclHelper
    {
        public void ResetSandboxAcl(string worktreePath, int timeoutMilliseconds)
        {
            inner.ResetSandboxAcl(worktreePath, timeoutMilliseconds);
            afterReset();
        }
    }

    private protected sealed class ThrowingTransactionalStateRepository : ITransactionalOrchestratorStateRepository
    {
        public int LoadCount { get; private set; }

        public int TransactionCount { get; private set; }

        public Task<AgentOrchestratorKernel> LoadAsync(CancellationToken cancellationToken = default)
        {
            LoadCount++;
            throw new InvalidOperationException("repo-process command should not load persistent state");
        }

        public Task<AgentOrchestratorKernel> LoadGoalsAsync(
            IReadOnlyCollection<GoalId> goalIds,
            CancellationToken cancellationToken = default)
        {
            LoadCount++;
            throw new InvalidOperationException("repo-process command should not load goal state");
        }

        public Task SaveAsync(AgentOrchestratorKernel kernel, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("repo-process command should not save persistent state");

        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("repo-process command should not list goal metadata");

        public Task<IReadOnlyList<GoalSummary>> ListConductLoopGoalMetadataAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("repo-process command should not list conduct metadata");

        public Task<IReadOnlyList<GoalId>> ListGoalIdsWithCompletedHumanInputAsync(
            IReadOnlyCollection<GoalId> goalIds,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("repo-process command should not query human input metadata");

        public Task<IReadOnlyList<ModelFitHistoryRow>> ListModelFitHistoryAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("repo-process command should not list model fit history");

        public Task<IReadOnlyList<ModelOutcomeRecord>> BuildModelOutcomeScorecardAsync(
            int windowSize = ModelOutcomeScorecard.DefaultWindowSize,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("repo-process command should not build model outcomes");

        public Task<ModelFitBestFit?> QueryBestFitForRoleAsync(AgentRole role, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("repo-process command should not query best fit");

        public Task<T> TransactAsync<T>(
            Func<AgentOrchestratorKernel, CancellationToken, Task<(bool ShouldSave, T Result)>> transaction,
            CancellationToken cancellationToken = default)
        {
            TransactionCount++;
            throw new InvalidOperationException("repo-process command should not transact persistent state");
        }

        public Task<T> TransactAsync<T>(
            Func<AgentOrchestratorKernel, Func<Task>, CancellationToken, Task<(bool ShouldSave, T Result)>> transaction,
            CancellationToken cancellationToken = default)
        {
            TransactionCount++;
            throw new InvalidOperationException("repo-process command should not transact persistent state");
        }

        public Task<GoalSnapshot?> LoadGoalAsync(GoalId goalId, CancellationToken cancellationToken = default)
        {
            LoadCount++;
            throw new InvalidOperationException("repo-process command should not load goal snapshots");
        }

        public Task SaveGoalSnapshotsAsync(
            IReadOnlyCollection<GoalSnapshot> goals,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("repo-process command should not save goal snapshots");

        public Task<T> TransactGoalAsync<T>(
            GoalId goalId,
            Func<GoalSnapshot?, CancellationToken, Task<(bool ShouldSave, GoalSnapshot? NewSnapshot, T Result)>> transaction,
            CancellationToken cancellationToken = default)
        {
            TransactionCount++;
            throw new InvalidOperationException("repo-process command should not transact goal state");
        }
    }

    private protected sealed class TestClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}

public sealed class GoalWorktreeReducedFixtureTests : GoalWorktreeTestBase
{
    [Xunit.Fact(DisplayName = "Reduced_seeded_repository_factory_creates_independent_current_state_stores")]
    public async Task ReducedSeededRepositoryFactoryCreatesIndependentCurrentStateStores()
    {
        var firstRepo = CreateReducedAcceptanceCohortRepository(renameInitialBranchToMain: false);
        var secondRepo = CreateReducedAcceptanceCohortRepository(renameInitialBranchToMain: false);
        try
        {
            var firstStatePath = OrchestratorWorkspace.ForDirectory(firstRepo).SqliteStatePath;
            var secondStatePath = OrchestratorWorkspace.ForDirectory(secondRepo).SqliteStatePath;
            Assert.NotEqual(firstRepo, secondRepo);
            Assert.NotEqual(firstStatePath, secondStatePath);
            Assert.True(StateDbMigrations.IsUpToDate(firstStatePath));
            Assert.True(StateDbMigrations.IsUpToDate(secondStatePath));

            var firstRepository = CreateMigratedStateRepository(firstStatePath);
            var firstKernel = await firstRepository.LoadAsync();
            var isolatedGoal = firstKernel.CreateGoal(
                "Persist only in the first reduced fixture",
                [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            await firstRepository.SaveAsync(firstKernel);

            var reloadedFirst = await CreateMigratedStateRepository(firstStatePath).LoadAsync();
            var reloadedSecond = await CreateMigratedStateRepository(secondStatePath).LoadAsync();
            Assert.Equal(isolatedGoal.Id, Assert.Single(reloadedFirst.Goals).Id);
            Assert.Empty(reloadedSecond.Goals);
        }
        finally
        {
            DeleteDirectory(firstRepo);
            DeleteDirectory(secondRepo);
        }
    }
}

public sealed class GoalWorktreeTestsAcceptanceRetry : GoalWorktreeTestBase
{
    [Xunit.Fact(DisplayName = "acceptance-retry_returns_failed_goal_to_verified_without_mutating_tasks")]
    public void AcceptanceRetryReturnsFailedGoalToVerifiedWithoutMutatingTasks()
    {
        var repo = CreateSeededRepository();
        try
        {
            const string operatorReason = "Build slot was locked by a stale test host.";
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Re-gate environmental acceptance failure", repo);
            var task = goal.Tasks.Single();
            var mainHead = RunGitOutput(repo, "rev-parse", "HEAD").Trim();
            Assert.Equal(1, kernel.RecordCriterionRetryFeedback(goal.Id, task.Id, ["prior automatic retry"]));
            Assert.True(kernel.BeginGoalAcceptanceVerification(goal.Id, "Run acceptance."));
            Assert.True(kernel.ReconcileGoalAcceptanceFailed(
                goal.Id,
                ["infrastructure tests: Cli"],
                "Acceptance failed in the environment.",
                branchHeadSha: mainHead,
                mainHeadSha: mainHead));

            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            OperatorInbox.RecordLandingEscalation(workspace, goal, "Acceptance failed.", "conductor:acceptance");
            var context = CreateAcceptanceContext(kernel, repo, goal);

            var output = CaptureConsole(() => CliCommandHandlers.Execute(
                ["acceptance-retry", goal.Id.Value[..8], "Build slot was locked", "by a stale test host.", "--confirm-acceptance-retry"],
                context));

            Assert.Contains("Acceptance retry scheduled", output);
            Assert.Equal(GoalStatus.Verified, goal.Status);
            Assert.Null(goal.LatestAcceptanceFailure);
            Assert.Equal(0, goal.AutomaticAcceptanceRetryCount);
            Assert.Equal(1, goal.OperatorAcceptanceRegateCount);
            Assert.Equal(WorkTaskStatus.Completed, task.Status);
            Assert.Null(task.LastDispatch);
            Assert.NotNull(task.LastVerification);

            var journalEntry = GoalOperationJournal.Read(repo, goal.Id).Entries
                .Last(entry => entry.Operation == "acceptance-retry");
            Assert.Equal(operatorReason, journalEntry.OperatorReason);
            Assert.Equal(mainHead, journalEntry.PriorGateMainSha);
            Assert.Equal(mainHead, journalEntry.CurrentHeadMainSha);
            Assert.Equal(1, journalEntry.OperatorRegateCount);

            using var escalationDocument = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(workspace.OrchestratorDirectory, "landing-escalations.json")));
            var escalation = escalationDocument.RootElement.GetProperty("items")[0];
            Assert.Equal(goal.Id.Value, escalation.GetProperty("goalId").GetString());
            Assert.Equal("acceptance-retry", escalation.GetProperty("resolvedBy").GetString());
            Assert.Equal(operatorReason, escalation.GetProperty("resolutionReason").GetString());
            Assert.False(escalation.GetProperty("resolvedAtUtc").ValueKind == JsonValueKind.Null);

            var restoredGoal = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot()).GetGoal(goal.Id);
            Assert.Equal(0, restoredGoal.AutomaticAcceptanceRetryCount);
            Assert.Equal(1, restoredGoal.OperatorAcceptanceRegateCount);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "acceptance-retry_requires_confirmation_before_goal_validation")]
    public void AcceptanceRetryRequiresConfirmationBeforeGoalValidation()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Guard acceptance retry", repo);
            var context = CreateAcceptanceContext(kernel, repo, goal);

            var usageError = Assert.Throws<ArgumentException>(() => CliCommandHandlers.Execute(
                ["acceptance-retry", goal.Id.Value[..8], "environment repaired"],
                context));
            Assert.Equal(CliCommandHelp.AcceptanceRetryUsage, usageError.Message);

            var stateError = Assert.Throws<InvalidOperationException>(() => CliCommandHandlers.Execute(
                ["acceptance-retry", goal.Id.Value[..8], "environment repaired", "--confirm-acceptance-retry"],
                context));
            Assert.Contains("is Verified, not AcceptanceFailed", stateError.Message);
            Assert.Equal(0, goal.OperatorAcceptanceRegateCount);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "acceptance-retry_names_incomplete_task_and_correct_recovery_verb")]
    public void AcceptanceRetryNamesIncompleteTaskAndCorrectRecoveryVerb()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Reject re-gate over failed work", repo);
            var task = goal.Tasks.Single();
            Assert.True(kernel.BeginGoalAcceptanceVerification(goal.Id, "Run acceptance."));
            Assert.True(kernel.ReconcileGoalAcceptanceFailed(
                goal.Id,
                ["environmental failure"],
                "Acceptance failed."));
            kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "Implementation is now known to be wrong.");

            var error = Assert.Throws<InvalidOperationException>(() => CliCommandHandlers.Execute(
                ["acceptance-retry", goal.Id.Value[..8], "try again", "--confirm-acceptance-retry"],
                CreateAcceptanceContext(kernel, repo, goal)));

            Assert.Contains("task 1 is Failed", error.Message);
            Assert.Contains($"retry {goal.Id.Value[..8]} 1 <reason>", error.Message);
            Assert.Equal(GoalStatus.AcceptanceFailed, goal.Status);
            Assert.NotNull(goal.LatestAcceptanceFailure);
            Assert.Equal(0, goal.OperatorAcceptanceRegateCount);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "acceptance-retry_requires_passed_verification_for_completed_tasks")]
    public void AcceptanceRetryRequiresPassedVerificationForCompletedTasks()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Reject completed work without a passed receipt", repo);
            Assert.True(kernel.BeginGoalAcceptanceVerification(goal.Id, "Run acceptance."));
            Assert.True(kernel.ReconcileGoalAcceptanceFailed(
                goal.Id,
                ["transient environment failure"],
                "Acceptance failed."));
            var snapshot = kernel.ExportSnapshot();
            var goalSnapshot = Assert.Single(snapshot.Goals);
            var taskSnapshot = Assert.Single(goalSnapshot.Tasks);
            var corruptKernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
            {
                Goals =
                [
                    goalSnapshot with
                    {
                        Tasks = [taskSnapshot with { LastVerification = null }]
                    }
                ]
            });

            var error = Assert.Throws<InvalidOperationException>(() =>
                corruptKernel.ValidateAcceptanceGateRetry(goal.Id, "environment repaired"));

            Assert.Contains("task 1", error.Message);
            Assert.Contains("Completed but has no verification", error.Message);
            Assert.Contains("verify-manual", error.Message);
            Assert.Equal(GoalStatus.AcceptanceFailed, corruptKernel.GetGoal(goal.Id).Status);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "acceptance-retry_requires_prior_gate_main_sha_before_mutating_goal")]
    public void AcceptanceRetryRequiresPriorGateMainShaBeforeMutatingGoal()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Reject unverifiable acceptance retry journal", repo);
            Assert.True(kernel.BeginGoalAcceptanceVerification(goal.Id, "Run acceptance."));
            Assert.True(kernel.ReconcileGoalAcceptanceFailed(
                goal.Id,
                ["environmental failure"],
                "Acceptance failed without a main SHA."));

            var error = Assert.Throws<InvalidOperationException>(() => CliCommandHandlers.Execute(
                ["acceptance-retry", goal.Id.Value[..8], "environment repaired", "--confirm-acceptance-retry"],
                CreateAcceptanceContext(kernel, repo, goal)));

            Assert.Contains("prior failing gate's main HEAD SHA", error.Message);
            Assert.Equal(GoalStatus.AcceptanceFailed, goal.Status);
            Assert.NotNull(goal.LatestAcceptanceFailure);
            Assert.Equal(0, goal.OperatorAcceptanceRegateCount);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "acceptance-retry_allows_cancelled_task_and_caps_operator_regates_at_three")]
    public void AcceptanceRetryAllowsCancelledTaskAndCapsOperatorRegatesAtThree()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Bound repeated environmental re-gates", repo);
            var task = goal.Tasks.Single();
            var context = CreateAcceptanceContext(kernel, repo, goal);
            var mainHead = RunGitOutput(repo, "rev-parse", "HEAD").Trim();

            for (var attempt = 1; attempt <= Goal.OperatorAcceptanceRegateCap; attempt++)
            {
                Assert.True(kernel.BeginGoalAcceptanceVerification(goal.Id, $"Acceptance attempt {attempt}."));
                Assert.True(kernel.ReconcileGoalAcceptanceFailed(
                    goal.Id,
                    ["transient environment failure"],
                    $"Acceptance attempt {attempt} failed.",
                    mainHeadSha: mainHead));
                if (attempt == 1)
                {
                    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Cancelled, "Operator deliberately descoped task.");
                    kernel.RecordTaskVerification(
                        goal.Id,
                        task.Id,
                        new TaskVerificationRecord(
                            "stale verification",
                            repo,
                            1,
                            string.Empty,
                            "Failed before the task was deliberately cancelled.",
                            DateTimeOffset.UtcNow));
                }

                OperatorInbox.RecordLandingEscalation(
                    context.Workspace,
                    goal,
                    $"Acceptance attempt {attempt} failed.",
                    "conductor:acceptance");
                CliCommandHandlers.Execute(
                    ["acceptance-retry", goal.Id.Value[..8], $"environment repair {attempt}", "--confirm-acceptance-retry"],
                    context);
                Assert.Equal(attempt, goal.OperatorAcceptanceRegateCount);
                Assert.Equal(WorkTaskStatus.Cancelled, task.Status);
            }

            Assert.True(kernel.BeginGoalAcceptanceVerification(goal.Id, "Acceptance attempt four."));
            Assert.True(kernel.ReconcileGoalAcceptanceFailed(
                goal.Id,
                ["persistent environment failure"],
                "Acceptance attempt four failed.",
                mainHeadSha: mainHead));

            var error = Assert.Throws<InvalidOperationException>(() => CliCommandHandlers.Execute(
                ["acceptance-retry", goal.Id.Value[..8], "fourth repair", "--confirm-acceptance-retry"],
                context));
            Assert.Contains("cap of 3 operator re-gates", error.Message);
            Assert.Equal(GoalStatus.AcceptanceFailed, goal.Status);
            Assert.Equal(Goal.OperatorAcceptanceRegateCap, goal.OperatorAcceptanceRegateCount);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "acceptance-retry_refuses_corrupt_escalation_store_without_mutating_goal")]
    public void AcceptanceRetryRefusesCorruptEscalationStoreWithoutMutatingGoal()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Preserve failed state when escalation audit cannot load", repo);
            var task = goal.Tasks.Single();
            var mainHead = RunGitOutput(repo, "rev-parse", "HEAD").Trim();
            Assert.True(kernel.BeginGoalAcceptanceVerification(goal.Id, "Run acceptance."));
            Assert.True(kernel.ReconcileGoalAcceptanceFailed(
                goal.Id,
                ["transient environment failure"],
                "Acceptance failed.",
                mainHeadSha: mainHead));
            var context = CreateAcceptanceContext(kernel, repo, goal);
            Directory.CreateDirectory(context.Workspace.OrchestratorDirectory);
            File.WriteAllText(
                Path.Combine(context.Workspace.OrchestratorDirectory, "landing-escalations.json"),
                "{ malformed");

            var error = Assert.Throws<InvalidOperationException>(() => CliCommandHandlers.Execute(
                ["acceptance-retry", goal.Id.Value[..8], "environment repaired", "--confirm-acceptance-retry"],
                context));

            Assert.Contains("Could not read landing escalation store", error.Message);
            Assert.Equal(GoalStatus.AcceptanceFailed, goal.Status);
            Assert.NotNull(goal.LatestAcceptanceFailure);
            Assert.Equal(0, goal.OperatorAcceptanceRegateCount);
            Assert.Equal(WorkTaskStatus.Completed, task.Status);
            Assert.DoesNotContain(
                GoalOperationJournal.Read(repo, goal.Id).Entries,
                entry => entry.Operation == "acceptance-retry");

            File.WriteAllText(
                Path.Combine(context.Workspace.OrchestratorDirectory, "landing-escalations.json"),
                """{"items":[]}""");
            var missingError = Assert.Throws<InvalidOperationException>(() => CliCommandHandlers.Execute(
                ["acceptance-retry", goal.Id.Value[..8], "environment repaired", "--confirm-acceptance-retry"],
                context));
            Assert.Contains("no unresolved landing escalation", missingError.Message);
            Assert.Equal(GoalStatus.AcceptanceFailed, goal.Status);
            Assert.Equal(0, goal.OperatorAcceptanceRegateCount);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "acceptance-retry_migrates_legacy_escalation_before_resolving_it")]
    public void AcceptanceRetryMigratesLegacyEscalationBeforeResolvingIt()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Resolve a pre-upgrade landing escalation", repo);
            var mainHead = RunGitOutput(repo, "rev-parse", "HEAD").Trim();
            Assert.True(kernel.BeginGoalAcceptanceVerification(goal.Id, "Run acceptance."));
            Assert.True(kernel.ReconcileGoalAcceptanceFailed(
                goal.Id,
                ["transient environment failure"],
                "Acceptance failed.",
                mainHeadSha: mainHead));
            var failureOccurredAt = goal.LatestAcceptanceFailure!.OccurredAt;
            var escalationPath = Path.Combine(workspace.OrchestratorDirectory, "landing-escalations.json");
            Directory.CreateDirectory(workspace.OrchestratorDirectory);
            File.WriteAllText(
                escalationPath,
                JsonSerializer.Serialize(new
                {
                    items = new[]
                    {
                        new
                        {
                            goalId = goal.Id.Value,
                            reason = "Acceptance failed.",
                            integrationBranch = "conductor:acceptance",
                            escalatedAt = DateTimeOffset.UtcNow,
                            resolvedAtUtc = (DateTimeOffset?)null,
                            resolvedBy = (string?)null,
                            resolutionReason = (string?)null
                        }
                    }
                }));

            CliCommandHandlers.Execute(
                ["acceptance-retry", goal.Id.Value[..8], "environment repaired", "--confirm-acceptance-retry"],
                CreateAcceptanceContext(kernel, repo, goal));

            using var document = JsonDocument.Parse(File.ReadAllText(escalationPath));
            var escalation = Assert.Single(document.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("acceptance-retry", escalation.GetProperty("resolvedBy").GetString());
            Assert.Equal(
                "environment repaired",
                escalation.GetProperty("resolutionReason").GetString());
            Assert.Equal(
                failureOccurredAt,
                escalation.GetProperty("acceptanceFailureOccurredAt").GetDateTimeOffset());
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "acceptance-retry_stale_acceptance_failed_snapshot_cannot_reopen_resolved_escalation")]
    public void AcceptanceRetryStaleAcceptanceFailedSnapshotCannotReopenResolvedEscalation()
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Do not reopen a resolved acceptance incident", repo);
            var mainHead = RunGitOutput(repo, "rev-parse", "HEAD").Trim();
            Assert.True(kernel.BeginGoalAcceptanceVerification(goal.Id, "Run acceptance."));
            Assert.True(kernel.ReconcileGoalAcceptanceFailed(
                goal.Id,
                ["transient environment failure"],
                "Acceptance failed.",
                mainHeadSha: mainHead));
            var staleGoal = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot()).GetGoal(goal.Id);
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            OperatorInbox.RecordLandingEscalation(
                workspace,
                goal,
                "Acceptance failed.",
                "conductor:acceptance");

            CliCommandHandlers.Execute(
                ["acceptance-retry", goal.Id.Value[..8], "environment repaired", "--confirm-acceptance-retry"],
                CreateAcceptanceContext(kernel, repo, goal));
            OperatorInbox.RecordLandingEscalation(
                workspace,
                staleGoal,
                "Acceptance failed.",
                "conductor:acceptance");

            using var escalationDocument = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(workspace.OrchestratorDirectory, "landing-escalations.json")));
            var escalation = Assert.Single(escalationDocument.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal("acceptance-retry", escalation.GetProperty("resolvedBy").GetString());
            Assert.NotEqual(JsonValueKind.Null, escalation.GetProperty("resolvedAtUtc").ValueKind);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "acceptance-retry_old_resolution_callback_does_not_resolve_newer_failure")]
    public async Task AcceptanceRetryOldResolutionCallbackDoesNotResolveNewerFailure()
    {
        var repo = CreateSeededRepository();
        var previousHook = OperatorInbox.BeforeLandingEscalationResolution;
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var firstFailureAt = new DateTimeOffset(2026, 7, 26, 4, 0, 0, TimeSpan.Zero);
            var kernel = new AgentOrchestratorKernel(new TestClock(firstFailureAt));
            var goal = CreateCompletedGoal(kernel, "Preserve a newer live acceptance incident", repo);
            var mainHead = RunGitOutput(repo, "rev-parse", "HEAD").Trim();
            Assert.True(kernel.BeginGoalAcceptanceVerification(goal.Id, "Run acceptance."));
            Assert.True(kernel.ReconcileGoalAcceptanceFailed(
                goal.Id,
                ["transient environment failure"],
                "Acceptance failed.",
                mainHeadSha: mainHead));
            var oldFailureOccurredAt = goal.LatestAcceptanceFailure!.OccurredAt;
            OperatorInbox.RecordLandingEscalation(
                workspace,
                goal,
                "Acceptance failed.",
                "conductor:acceptance");

            var newerKernel = AgentOrchestratorKernel.FromSnapshot(
                kernel.ExportSnapshot(),
                new TestClock(firstFailureAt.AddMinutes(1)));
            Assert.Equal(1, newerKernel.RetryAcceptanceGate(goal.Id, "First environment repair."));
            Assert.True(newerKernel.BeginGoalAcceptanceVerification(goal.Id, "Re-run acceptance."));
            Assert.True(newerKernel.ReconcileGoalAcceptanceFailed(
                goal.Id,
                ["second transient environment failure"],
                "Acceptance failed again.",
                mainHeadSha: mainHead));
            var newerGoal = newerKernel.GetGoal(goal.Id);
            var newerFailureOccurredAt = newerGoal.LatestAcceptanceFailure!.OccurredAt;
            Assert.NotEqual(oldFailureOccurredAt, newerFailureOccurredAt);

            using var resolutionEntered = new ManualResetEventSlim();
            using var newerEscalationRecorded = new ManualResetEventSlim();
            OperatorInbox.BeforeLandingEscalationResolution = () =>
            {
                resolutionEntered.Set();
                Assert.True(newerEscalationRecorded.Wait(TimeSpan.FromSeconds(10)));
            };

            var resolutionTask = Task.Run(() => OperatorInbox.ResolveLandingEscalation(
                workspace,
                goal,
                "First environment repair.",
                acceptanceFailureOccurredAt: oldFailureOccurredAt));
            Assert.True(resolutionEntered.Wait(TimeSpan.FromSeconds(10)));
            OperatorInbox.RecordLandingEscalation(
                workspace,
                newerGoal,
                "Acceptance failed again.",
                "conductor:acceptance");
            newerEscalationRecorded.Set();

            Assert.False(await resolutionTask);
            using var escalationDocument = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(workspace.OrchestratorDirectory, "landing-escalations.json")));
            var escalation = Assert.Single(escalationDocument.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(newerFailureOccurredAt, escalation.GetProperty("acceptanceFailureOccurredAt").GetDateTimeOffset());
            Assert.Equal(JsonValueKind.Null, escalation.GetProperty("resolvedAtUtc").ValueKind);
        }
        finally
        {
            OperatorInbox.BeforeLandingEscalationResolution = previousHook;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "acceptance-retry_outbox_rolls_back_with_commit_and_replays_pending_audit")]
    public void AcceptanceRetryOutboxRollsBackWithCommitAndReplaysPendingAudit()
    {
        var repo = CreateSeededRepository();
        var previousResolutionHook = OperatorInbox.BeforeLandingEscalationResolution;
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var failOutboxCommit = false;
            var repository = CreateMigratedStateRepository(
                workspace.SqliteStatePath,
                statementObserver: null,
                telemetryOptions: null,
                beforeOutboxCommit: () =>
                {
                    if (failOutboxCommit)
                    {
                        throw new IOException("Injected SQLite commit failure.");
                    }
                });
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Roll back re-gate when audit persistence fails", repo);
            var mainHead = RunGitOutput(repo, "rev-parse", "HEAD").Trim();
            Assert.True(kernel.BeginGoalAcceptanceVerification(goal.Id, "Run acceptance."));
            Assert.True(kernel.ReconcileGoalAcceptanceFailed(
                goal.Id,
                ["transient environment failure"],
                "Acceptance failed.",
                mainHeadSha: mainHead));
            OperatorInbox.RecordLandingEscalation(
                workspace,
                goal,
                "Acceptance failed.",
                "conductor:acceptance");
            repository.SaveAsync(kernel).GetAwaiter().GetResult();
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = goal;

            failOutboxCommit = true;
            var commitError = Assert.Throws<IOException>(() => CliPersistentStateRunner.ExecuteCommand(
                ["acceptance-retry", goal.Id.Value[..8], "environment repaired", "--confirm-acceptance-retry"],
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal));
            Assert.Contains("Injected SQLite commit failure", commitError.Message);
            AssertAcceptanceRetryRolledBack(repository, goal.Id);
            Assert.Empty(repository
                .ListOutboxMessagesAsync(GoalOperationJournal.AcceptanceRetryAuditOutboxKind)
                .GetAwaiter()
                .GetResult());
            Assert.DoesNotContain(
                GoalOperationJournal.Read(repo, goal.Id).Entries,
                entry => entry.Operation == "acceptance-retry");
            using (var unresolvedDocument = JsonDocument.Parse(
                       File.ReadAllText(Path.Combine(workspace.OrchestratorDirectory, "landing-escalations.json"))))
            {
                var unresolved = Assert.Single(
                    unresolvedDocument.RootElement.GetProperty("items").EnumerateArray());
                Assert.Equal(JsonValueKind.Null, unresolved.GetProperty("resolvedAtUtc").ValueKind);
            }

            failOutboxCommit = false;
            OperatorInbox.BeforeLandingEscalationResolution = () =>
                throw new IOException("Injected post-commit audit failure.");
            var changedDespiteAuditError = CliPersistentStateRunner.ExecuteCommand(
                ["acceptance-retry", goal.Id.Value[..8], "environment repaired", "--confirm-acceptance-retry"],
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);
            Assert.True(changedDespiteAuditError);
            var committedGoal = repository.LoadAsync().GetAwaiter().GetResult().GetGoal(goal.Id);
            Assert.Equal(GoalStatus.Verified, committedGoal.Status);
            Assert.Null(committedGoal.LatestAcceptanceFailure);
            Assert.Equal(1, committedGoal.OperatorAcceptanceRegateCount);
            Assert.Single(repository
                .ListOutboxMessagesAsync(GoalOperationJournal.AcceptanceRetryAuditOutboxKind)
                .GetAwaiter()
                .GetResult());

            OperatorInbox.BeforeLandingEscalationResolution = null;
            var newerKernel = repository.LoadAsync().GetAwaiter().GetResult();
            Assert.True(newerKernel.BeginGoalAcceptanceVerification(goal.Id, "Run acceptance again."));
            Assert.True(newerKernel.ReconcileGoalAcceptanceFailed(
                goal.Id,
                ["second transient environment failure"],
                "Acceptance failed again.",
                mainHeadSha: mainHead));
            var newerGoal = newerKernel.GetGoal(goal.Id);
            var newerFailureOccurredAt = newerGoal.LatestAcceptanceFailure!.OccurredAt;
            OperatorInbox.RecordLandingEscalation(
                workspace,
                newerGoal,
                "Acceptance failed again.",
                "conductor:acceptance");
            repository.SaveAsync(newerKernel).GetAwaiter().GetResult();

            var changed = CliPersistentStateRunner.ExecuteCommand(
                ["goals"],
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);

            Assert.False(changed);
            Assert.Empty(repository
                .ListOutboxMessagesAsync(GoalOperationJournal.AcceptanceRetryAuditOutboxKind)
                .GetAwaiter()
                .GetResult());
            Assert.Single(
                GoalOperationJournal.Read(repo, goal.Id).Entries,
                entry => entry.Operation == "acceptance-retry");
            using var resolvedDocument = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(workspace.OrchestratorDirectory, "landing-escalations.json")));
            var currentEscalation = Assert.Single(
                resolvedDocument.RootElement.GetProperty("items").EnumerateArray());
            Assert.Equal(
                newerFailureOccurredAt,
                currentEscalation.GetProperty("acceptanceFailureOccurredAt").GetDateTimeOffset());
            Assert.Equal(JsonValueKind.Null, currentEscalation.GetProperty("resolvedAtUtc").ValueKind);
        }
        finally
        {
            OperatorInbox.BeforeLandingEscalationResolution = previousResolutionHook;
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "acceptance-retry_outbox_quarantines_poison_without_blocking_unrelated_command")]
    public void AcceptanceRetryOutboxQuarantinesPoisonWithoutBlockingUnrelatedCommand()
    {
        var repo = CreateSeededRepository();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
            var orphanKernel = new AgentOrchestratorKernel();
            var orphanGoal = orphanKernel.CreateGoal("Orphaned acceptance retry audit");
            var messages = new OrchestratorStateOutboxMessage[]
            {
                new(
                    "acceptance-retry-audit:malformed",
                    GoalOperationJournal.AcceptanceRetryAuditOutboxKind,
                    "{ malformed",
                    DateTimeOffset.UtcNow),
                GoalOperationJournal.CreateAcceptanceRetryAuditMessage(
                    orphanGoal,
                    "environment repaired",
                    new string('a', 40),
                    new string('b', 40),
                    1,
                    DateTimeOffset.UtcNow)
            };
            repository.TransactWithOutboxAsync(
                    (_, _) => Task.FromResult((
                        ShouldSave: false,
                        Result: 0,
                        OutboxMessages: (IReadOnlyList<OrchestratorStateOutboxMessage>)messages)))
                .GetAwaiter()
                .GetResult();

            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var providers = new InMemoryModelProviderRegistry([]);
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            var changed = CliPersistentStateRunner.ExecuteCommand(
                ["goals"],
                repository,
                workspace,
                ref agents,
                providers,
                ref profiles,
                ref currentGoal);

            Assert.False(changed);
            Assert.Empty(repository
                .ListOutboxMessagesAsync(GoalOperationJournal.AcceptanceRetryAuditOutboxKind)
                .GetAwaiter()
                .GetResult());
            using var connection = new SqliteConnection(
                $"Data Source={workspace.SqliteStatePath};Mode=ReadOnly;Pooling=False;");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*)
                FROM state_outbox
                WHERE quarantined_at IS NOT NULL
                  AND quarantine_reason IS NOT NULL
                """;
            Assert.Equal(2L, (long)command.ExecuteScalar()!);
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    [Xunit.Fact(DisplayName = "acceptance-retry_outbox_simultaneous_drainers_apply_audit_once")]
    public async Task AcceptanceRetryOutboxSimultaneousDrainersApplyAuditOnce()
    {
        var repo = CreateSeededRepository();
        var previousJournalHook = GoalOperationJournal.BeforeAcceptanceRetryAppend;
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateCompletedGoal(kernel, "Claim acceptance retry audit once", repo);
            var mainHead = RunGitOutput(repo, "rev-parse", "HEAD").Trim();
            Assert.True(kernel.BeginGoalAcceptanceVerification(goal.Id, "Run acceptance."));
            Assert.True(kernel.ReconcileGoalAcceptanceFailed(
                goal.Id,
                ["transient environment failure"],
                "Acceptance failed.",
                mainHeadSha: mainHead));
            OperatorInbox.RecordLandingEscalation(
                workspace,
                goal,
                "Acceptance failed.",
                "conductor:acceptance");
            repository.SaveAsync(kernel).GetAwaiter().GetResult();

            GoalOperationJournal.BeforeAcceptanceRetryAppend = () =>
                throw new IOException("Leave the committed audit pending.");
            RunPersistentCommand(
                repository,
                workspace,
                ["acceptance-retry", goal.Id.Value[..8], "environment repaired", "--confirm-acceptance-retry"]);
            Assert.Single(repository
                .ListOutboxMessagesAsync(GoalOperationJournal.AcceptanceRetryAuditOutboxKind)
                .GetAwaiter()
                .GetResult());

            using var firstDrainerEnteredJournal = new ManualResetEventSlim();
            using var releaseFirstDrainer = new ManualResetEventSlim();
            var journalHookCalls = 0;
            GoalOperationJournal.BeforeAcceptanceRetryAppend = () =>
            {
                Assert.Equal(1, Interlocked.Increment(ref journalHookCalls));
                firstDrainerEnteredJournal.Set();
                Assert.True(releaseFirstDrainer.Wait(TimeSpan.FromSeconds(10)));
            };

            var firstDrainer = Task.Run(() => RunPersistentCommand(repository, workspace, ["goals"]));
            Assert.True(firstDrainerEnteredJournal.Wait(TimeSpan.FromSeconds(10)));
            var secondDrainerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondDrainer = Task.Run(() =>
            {
                secondDrainerStarted.SetResult();
                return RunPersistentCommand(
                    // The primary helper already migrated this store; a competing drainer must not run DDL.
                    new SqliteOrchestratorStateRepository(workspace.SqliteStatePath),
                    workspace,
                    ["goals"]);
            });
            await secondDrainerStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            releaseFirstDrainer.Set();

            Assert.False(await firstDrainer);
            Assert.False(await secondDrainer);
            Assert.Equal(1, journalHookCalls);
            Assert.Empty(await repository.ListOutboxMessagesAsync(
                GoalOperationJournal.AcceptanceRetryAuditOutboxKind));
            Assert.Single(
                GoalOperationJournal.Read(repo, goal.Id).Entries,
                entry => entry.Operation == "acceptance-retry");
        }
        finally
        {
            GoalOperationJournal.BeforeAcceptanceRetryAppend = previousJournalHook;
            DeleteDirectory(repo);
        }
    }

    private static bool RunPersistentCommand(
        ITransactionalOrchestratorStateRepository repository,
        OrchestratorWorkspace workspace,
        IReadOnlyList<string> args)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        return CliPersistentStateRunner.ExecuteCommand(
            args,
            repository,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal);
    }

    private static void AssertAcceptanceRetryRolledBack(
        SqliteOrchestratorStateRepository repository,
        GoalId goalId)
    {
        var committedGoal = repository.LoadAsync().GetAwaiter().GetResult().GetGoal(goalId);
        Assert.Equal(GoalStatus.AcceptanceFailed, committedGoal.Status);
        Assert.NotNull(committedGoal.LatestAcceptanceFailure);
        Assert.Equal(0, committedGoal.OperatorAcceptanceRegateCount);
    }
}

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class GoalWorktreeIsolatedDotnetTests : GoalWorktreeTestBase
{
    // Goal receipts after the minimal-probe conversion complete in 0.66-0.91s; this is a hang guard, not a performance bound.
    private static readonly TimeSpan RealProcessExitHangGuard = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ProcessReadinessHangGuard = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RealProcessControlShortHangGuard = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RealProcessControlCompletionDelay = TimeSpan.FromSeconds(5);
    private const string ProbeProjectName = "Mcg.AgentOrchestrator.IsolatedDotnetProbe";
    private const string ProbeProject = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Fixtures/IsolatedDotnetProbe/Mcg.AgentOrchestrator.IsolatedDotnetProbe.csproj";

    [Xunit.Fact(DisplayName = "InvokeIsolatedDotnet_reuses_prebuilt_test_assembly_and_dependency_directory")]
    public async Task InvokeIsolatedDotnetReusesPrebuiltTestAssemblyAndDependencyDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fixture = CreateReuseFixture(includeAssembly: true);
        try
        {
            var result = await RunReusePassAsync(fixture);

            Assert.True(
                result.ExitCode == 0,
                $"Invoke-IsolatedDotnet.ps1 exited {result.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{result.Stdout}{Environment.NewLine}stderr:{Environment.NewLine}{result.Stderr}");
            Assert.Contains(fixture.AssemblyPath, result.Stdout, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(fixture.ExecutablePath, result.Stdout, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(fixture.DependencyDirectory, result.Stdout, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(fixture.AssemblyPath), "The reuse pass must preserve the pre-built test assembly.");
            Assert.True(File.Exists(fixture.ExecutablePath), "The reuse pass must preserve the pre-built MTP executable.");
            Assert.True(
                File.Exists(Path.Combine(fixture.DependencyDirectory, $"{ProbeProjectName}.deps.json")),
                "The reuse pass must preserve the test assembly dependency directory.");
            var probeArguments = File.ReadAllLines(fixture.ProbeReceiptPath);
            Assert.Contains("--filter-class", probeArguments);
            Assert.Contains("*IsolatedDotnetProbe*", probeArguments);
            Assert.Contains("--no-ansi", probeArguments);
            Assert.Contains("--progress", probeArguments);
            Assert.Contains("off", probeArguments);

            var log = File.ReadAllText(fixture.DotnetLogPath);
            Assert.DoesNotContain("args=test ", log, StringComparison.Ordinal);
            Assert.DoesNotContain("args=build ", log, StringComparison.Ordinal);
            Assert.Contains("args=build-server shutdown", log, StringComparison.Ordinal);
            Assert.Contains($"args={fixture.AssemblyPath} ", log, StringComparison.OrdinalIgnoreCase);

            var source = File.ReadAllText(Path.Combine(FindCurrentSourceRoot(), "scripts", "Invoke-IsolatedDotnet.ps1"));
            Assert.Contains(".SYNOPSIS", source, StringComparison.Ordinal);
            Assert.Contains(".DESCRIPTION", source, StringComparison.Ordinal);
            Assert.True(source.Split(".EXAMPLE", StringSplitOptions.None).Length >= 3);
            Assert.Contains("-ReuseArtifacts", source, StringComparison.Ordinal);
            Assert.Contains("--no-build", source, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [Xunit.Fact(DisplayName = "InvokeIsolatedDotnet_reuse_fails_loudly_when_test_assembly_is_missing")]
    public async Task InvokeIsolatedDotnetReuseFailsLoudlyWhenTestAssemblyIsMissing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fixture = CreateReuseFixture(includeAssembly: false);
        try
        {
            var result = await RunReusePassAsync(fixture);

            Assert.Equal(86, result.ExitCode);
            Assert.Contains("Artifact reuse precondition failed", result.Stderr, StringComparison.Ordinal);
            Assert.Contains(fixture.AssemblyPath, result.Stderr, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Expected owner token: 'goal-reuse-goal'", result.Stderr, StringComparison.Ordinal);
            Assert.Contains("found owner token: 'goal-reuse-goal'", result.Stderr, StringComparison.Ordinal);
            Assert.Contains(
                $@".\scripts\Invoke-IsolatedDotnet.ps1 -GoalPrefix reuse-goal build {ProbeProject} --configuration Debug --verbosity minimal",
                result.Stderr,
                StringComparison.Ordinal);

            var log = File.ReadAllText(fixture.DotnetLogPath);
            Assert.DoesNotContain("args=test ", log, StringComparison.Ordinal);
            Assert.Contains("args=build-server shutdown", log, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [Xunit.Fact(DisplayName = "InvokeIsolatedDotnet_reuse_requires_the_generated_apphost_as_an_artifact_invariant")]
    public async Task InvokeIsolatedDotnetReuseRequiresGeneratedApphost()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fixture = CreateReuseFixture(includeAssembly: true, includeExecutable: false);
        try
        {
            var result = await RunReusePassAsync(fixture);

            Assert.Equal(86, result.ExitCode);
            Assert.Contains("The reusable Microsoft.Testing.Platform executable was not found.", result.Stderr, StringComparison.Ordinal);
            Assert.Contains(fixture.ExecutablePath, result.Stderr, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(fixture.ProbeReceiptPath));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [Xunit.Fact(DisplayName = "InvokeIsolatedDotnet_reuse_propagates_dotnet_host_exit_code")]
    public async Task InvokeIsolatedDotnetReusePropagatesDotnetHostExitCode()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const int expectedExitCode = 37;
        var fixture = CreateReuseFixture(includeAssembly: true);
        try
        {
            var result = await RunReusePassAsync(fixture, probeExitCode: expectedExitCode);

            Assert.Equal(expectedExitCode, result.ExitCode);
            Assert.True(File.Exists(fixture.ProbeReceiptPath));
            Assert.Contains(
                $"args={fixture.AssemblyPath} ",
                File.ReadAllText(fixture.DotnetLogPath),
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [Xunit.Fact(DisplayName = "Reuse_hang_guard_fires_when_real_dependency_outlives_injected_budget")]
    public async Task ReuseHangGuardFiresWhenRealDependencyOutlivesInjectedBudget()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fixture = CreateReuseFixture(includeAssembly: true, shimDelay: RealProcessControlCompletionDelay);
        try
        {
            Assert.True(
                RealProcessControlCompletionDelay > RealProcessControlShortHangGuard,
                "The real shim delay must exceed the injected guard by construction.");

            var exception = await Assert.ThrowsAsync<RealProcessHangGuardException>(
                () => RunReusePassAsync(
                    fixture,
                    RealProcessControlShortHangGuard,
                    waitForShimReadiness: true));

            Assert.Contains(
                $"{RealProcessControlShortHangGuard.TotalSeconds:0} seconds",
                exception.Message,
                StringComparison.Ordinal);
            Assert.Contains("elapsed=", exception.Message, StringComparison.Ordinal);
            Assert.True(exception.RootExited, "The short-guard PowerShell root must be reaped.");
            Assert.True(exception.DescendantExited, "The delayed shim descendant must be reaped.");

            var result = await RunReusePassAsync(fixture);

            Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
            Assert.Contains(
                $"delaySeconds={RealProcessControlCompletionDelay.TotalSeconds:0}",
                File.ReadAllText(fixture.DotnetLogPath),
                StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [Xunit.Fact(DisplayName = "Reuse_hang_guard_fails_and_reaps_when_dependency_never_exits")]
    public async Task ReuseHangGuardFailsAndReapsWhenDependencyNeverExits()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var guard = TimeSpan.FromSeconds(2);
        var fixture = CreateReuseFixture(includeAssembly: true, shimNeverExits: true);
        try
        {
            var exception = await Assert.ThrowsAsync<RealProcessHangGuardException>(
                () => RunReusePassAsync(fixture, guard, waitForShimReadiness: true));

            Assert.Contains($"{guard.TotalSeconds:0} seconds", exception.Message, StringComparison.Ordinal);
            Assert.Contains("elapsed=", exception.Message, StringComparison.Ordinal);
            Assert.True(exception.RootExited, "The guarded PowerShell root must be reaped.");
            Assert.True(exception.DescendantExited, "The never-exiting shim descendant must be reaped.");
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    private static ReuseFixture CreateReuseFixture(
        bool includeAssembly,
        TimeSpan? shimDelay = null,
        bool shimNeverExits = false,
        bool includeExecutable = true)
    {
        Assert.False(shimDelay.HasValue && shimNeverExits, "A shim cannot be delayed and never-exiting at the same time.");
        var root = Path.Combine(Path.GetTempPath(), "mcg-isolated-dotnet-reuse-tests", Guid.NewGuid().ToString("N"));
        var isolatedRoot = Path.Combine(root, "isolated");
        var shimDirectory = Path.Combine(root, "shim");
        var workDirectory = Path.Combine(root, "repo");
        var artifactsPath = Path.Combine(isolatedRoot, "goals", "reuse-goal", "artifacts");
        var dependencyDirectory = Path.Combine(artifactsPath, "bin", ProbeProjectName, "debug");
        var assemblyPath = Path.Combine(dependencyDirectory, $"{ProbeProjectName}.dll");
        var executablePath = Path.Combine(dependencyDirectory, $"{ProbeProjectName}.exe");
        var dotnetLogPath = Path.Combine(root, "dotnet.log");
        var probeReceiptPath = Path.Combine(root, "mtp-probe.txt");
        var shimReadyPath = Path.Combine(root, "dotnet-shim.ready");
        var shimChildPidPath = Path.Combine(root, "dotnet-shim-child.pid");

        Directory.CreateDirectory(shimDirectory);
        Directory.CreateDirectory(workDirectory);
        Directory.CreateDirectory(dependencyDirectory);
        File.WriteAllText(
            Path.Combine(artifactsPath, ".mcg-artifacts-owner.json"),
            JsonSerializer.Serialize(new
            {
                version = 1,
                ownerToken = "goal-reuse-goal",
                ownerProcessId = 123456789,
                machineName = Environment.MachineName,
                lastAcquiredAt = DateTimeOffset.UtcNow
            }));
        CopyDirectory(ResolveProbeOutputDirectory(), dependencyDirectory);
        Assert.True(File.Exists(assemblyPath), $"Probe output is missing {assemblyPath}.");
        Assert.True(File.Exists(executablePath), $"Probe output is missing {executablePath}.");
        if (!includeAssembly)
        {
            File.Delete(assemblyPath);
        }
        if (!includeExecutable)
        {
            File.Delete(executablePath);
        }

        var shimLines = new List<string>
        {
            "@echo off",
            ">> \"%DOTNET_SHIM_LOG%\" echo args=%*",
            "if not \"%~1\"==\"build-server\" goto run-real-dotnet"
        };
        if (shimDelay.HasValue)
        {
            shimLines.Add($">> \"%DOTNET_SHIM_LOG%\" echo delaySeconds={shimDelay.Value.TotalSeconds:0}");
            shimLines.Add(
                $"powershell.exe -NoProfile -NonInteractive -Command \"Set-Content -LiteralPath $env:DOTNET_SHIM_CHILD_PID -Value $PID; " +
                $"Set-Content -LiteralPath $env:DOTNET_SHIM_READY_PATH -Value ready; Start-Sleep -Seconds {shimDelay.Value.TotalSeconds:0}\"");
        }
        else if (shimNeverExits)
        {
            shimLines.Add(
                "powershell.exe -NoProfile -NonInteractive -Command \"Set-Content -LiteralPath $env:DOTNET_SHIM_CHILD_PID -Value $PID; " +
                "Set-Content -LiteralPath $env:DOTNET_SHIM_READY_PATH -Value ready; while ($true) { Start-Sleep -Seconds 60 }\"");
        }
        shimLines.Add("exit /b 0");
        shimLines.Add(":run-real-dotnet");
        shimLines.Add("set \"PATH=%DOTNET_REAL_PATH%\"");
        shimLines.Add("dotnet.exe %*");
        shimLines.Add("exit /b %ERRORLEVEL%");
        File.WriteAllLines(Path.Combine(shimDirectory, "dotnet.cmd"), shimLines);

        return new ReuseFixture(
            root,
            isolatedRoot,
            shimDirectory,
            workDirectory,
            artifactsPath,
            dependencyDirectory,
            assemblyPath,
            executablePath,
            dotnetLogPath,
            probeReceiptPath,
            shimReadyPath,
            shimChildPidPath);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunReusePassAsync(
        ReuseFixture fixture,
        TimeSpan? hangGuard = null,
        bool waitForShimReadiness = false,
        int? probeExitCode = null)
    {
        var activeHangGuard = hangGuard ?? RealProcessExitHangGuard;
        var startInfo = new ProcessStartInfo
        {
            FileName = WorkerShell.Executable,
            WorkingDirectory = fixture.WorkDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-InputFormat");
        startInfo.ArgumentList.Add("None");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(Path.Combine(FindCurrentSourceRoot(), "scripts", "Invoke-IsolatedDotnet.ps1"));
        startInfo.ArgumentList.Add("-GoalPrefix");
        startInfo.ArgumentList.Add("reuse-goal");
        startInfo.ArgumentList.Add("-ReuseArtifacts");
        startInfo.ArgumentList.Add("test");
        startInfo.ArgumentList.Add(ProbeProject);
        startInfo.ArgumentList.Add("--no-build");
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add("Debug");
        startInfo.ArgumentList.Add("--verbosity");
        startInfo.ArgumentList.Add("minimal");
        startInfo.ArgumentList.Add("--filter");
        startInfo.ArgumentList.Add("FullyQualifiedName~IsolatedDotnetProbe");
        var realDotnetPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        startInfo.Environment["PATH"] = fixture.ShimDirectory + Path.PathSeparator + realDotnetPath;
        startInfo.Environment["DOTNET_REAL_PATH"] = realDotnetPath;
        startInfo.Environment["DOTNET_SHIM_LOG"] = fixture.DotnetLogPath;
        startInfo.Environment["DOTNET_SHIM_READY_PATH"] = fixture.ShimReadyPath;
        startInfo.Environment["DOTNET_SHIM_CHILD_PID"] = fixture.ShimChildPidPath;
        startInfo.Environment["MCG_ISOLATED_DOTNET_MTP_PROBE_PATH"] = fixture.ProbeReceiptPath;
        if (probeExitCode.HasValue)
        {
            startInfo.Environment["MCG_ISOLATED_DOTNET_MTP_PROBE_EXIT_CODE"] = probeExitCode.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        startInfo.Environment[DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable] = fixture.IsolatedRoot;
        startInfo.Environment.Remove(WorkerSandboxOptions.DispatchWorkerVariable);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start PowerShell.");
        process.StandardInput.Close();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        Process? descendant = null;
        if (waitForShimReadiness)
        {
            try
            {
                await WaitForFilesAsync(
                    fixture.Root,
                    [fixture.ShimReadyPath, fixture.ShimChildPidPath],
                    ProcessReadinessHangGuard);
            }
            catch
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
                await process.WaitForExitAsync();
                await stdoutTask;
                await stderrTask;
                throw;
            }
            descendant = Process.GetProcessById(int.Parse(File.ReadAllText(fixture.ShimChildPidPath).Trim(), System.Globalization.CultureInfo.InvariantCulture));
        }

        var stopwatch = Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(activeHangGuard);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync();
            var descendantExited = descendant is null || descendant.WaitForExit(10_000);
            var timedOutStdout = await stdoutTask;
            var timedOutStderr = await stderrTask;
            throw new RealProcessHangGuardException(
                $"Invoke-IsolatedDotnet.ps1 did not exit within {activeHangGuard.TotalSeconds:0} seconds; elapsed={stopwatch.Elapsed}.{Environment.NewLine}stdout:{Environment.NewLine}{timedOutStdout}{Environment.NewLine}stderr:{Environment.NewLine}{timedOutStderr}",
                process.HasExited,
                descendantExited);
        }
        finally
        {
            descendant?.Dispose();
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return (process.ExitCode, stdout, stderr);
    }

    private static async Task WaitForFilesAsync(string directory, string[] paths, TimeSpan hangGuard)
    {
        if (paths.All(File.Exists))
        {
            return;
        }

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(directory)
        {
            EnableRaisingEvents = true,
            IncludeSubdirectories = false
        };
        FileSystemEventHandler checkReady = (_, _) =>
        {
            if (paths.All(File.Exists))
            {
                ready.TrySetResult();
            }
        };
        watcher.Created += checkReady;
        watcher.Changed += checkReady;
        if (paths.All(File.Exists))
        {
            return;
        }

        using var timeout = new CancellationTokenSource(hangGuard);
        using var registration = timeout.Token.Register(
            () => ready.TrySetException(new Xunit.Sdk.XunitException(
                $"Real-process readiness files did not arrive within {hangGuard.TotalSeconds:0} seconds: {string.Join(", ", paths)}")));
        await ready.Task;
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static string ResolveProbeOutputDirectory()
    {
        var testOutput = new DirectoryInfo(AppContext.BaseDirectory);
        var configurationDirectory = testOutput.Name.StartsWith("net", StringComparison.OrdinalIgnoreCase)
            ? testOutput.Parent
            : testOutput;
        var artifactsBin = configurationDirectory?.Parent?.Parent;
        var isolatedOutput = artifactsBin is null
            ? null
            : Path.Combine(artifactsBin.FullName, ProbeProjectName, configurationDirectory!.Name);
        if (isolatedOutput is not null && File.Exists(Path.Combine(isolatedOutput, $"{ProbeProjectName}.exe")))
        {
            return isolatedOutput;
        }

        var configuration = configurationDirectory?.Name ?? "Debug";
        var conventionalRoot = Path.Combine(
            FindCurrentSourceRoot(),
            "tests",
            "Mcg.AgentOrchestrator.Infrastructure.Tests",
            "Fixtures",
            "IsolatedDotnetProbe",
            "bin",
            configuration);
        var candidates = Directory.Exists(conventionalRoot)
            ? Directory.EnumerateFiles(conventionalRoot, $"{ProbeProjectName}.exe", SearchOption.AllDirectories)
                .Select(Path.GetDirectoryName)
                .Where(path => path is not null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];
        return Assert.Single(candidates)!;
    }

    private sealed record ReuseFixture(
        string Root,
        string IsolatedRoot,
        string ShimDirectory,
        string WorkDirectory,
        string ArtifactsPath,
        string DependencyDirectory,
        string AssemblyPath,
        string ExecutablePath,
        string DotnetLogPath,
        string ProbeReceiptPath,
        string ShimReadyPath,
        string ShimChildPidPath);

    private sealed class RealProcessHangGuardException(
        string message,
        bool rootExited,
        bool descendantExited) : Xunit.Sdk.XunitException(message)
    {
        public bool RootExited { get; } = rootExited;
        public bool DescendantExited { get; } = descendantExited;
    }
}

[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class GoalWorktreeAcceptanceContentionTests : GoalWorktreeTestBase
{
    [Xunit.Fact(DisplayName = "GoalWorktree_acceptance_contention_reconciles_blocked_attempt_before_clean_regate")]
    public void GoalWorktreeAcceptanceContentionReconcilesBlockedAttemptBeforeCleanRegate()
    {
        using var isolatedRoot = new IsolatedDotnetRootScope();
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateCompletedGoal(kernel, "Verify clean acceptance re-gate.", Environment.CurrentDirectory);
        var candidate = ConductorParallelAcceptanceCandidate.Create(
            goal,
            0,
            ["src/Regate.cs"],
            "branch",
            "main");
        var environment = DotnetBuildEnvironmentManager.CreateAttempt(goal.Id, "contention-incumbent");
        var buildPermit = environment.BuildPermitIndex
            ?? throw new InvalidOperationException("Goal build permit was not assigned.");
        var attemptRoot = Path.Combine(Path.GetTempPath(), $"mcg-regate-{Guid.NewGuid():N}");
        var permitLeases = new List<FileStream>();

        try
        {
            ConductorParallelAcceptanceAttemptDecision blocked;
            try
            {
                foreach (var permitIndex in Enumerable.Range(0, DotnetBuildEnvironmentManager.BuildConcurrencySlotCount))
                {
                    permitLeases.Add(DotnetBuildEnvironmentManager.AcquireLeaseExecutionLock(
                        DotnetBuildEnvironmentManager.CreateStableSlotAttempt(permitIndex),
                        TimeSpan.Zero));
                }

                var blockedCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                    attemptRoot,
                    runInline: true,
                    buildPermitBusyTimeout: TimeSpan.Zero);
                blocked = blockedCoordinator.Evaluate(
                    candidate,
                    ConductorAutonomyPolicy.Conservative,
                    (attemptCandidate, _, _, _) => ConductorParallelAcceptanceRunResult.Accepted(
                        attemptCandidate,
                        AcceptanceVerificationSummary.PassedWithNoUnmetCriteria));

                Xunit.Assert.Equal(
                    ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot,
                    blocked.Attempt.Outcome);
                Xunit.Assert.Equal(
                    AcceptanceBuildPermitWaitReason.AllPermitsBusy,
                    blocked.Attempt.BuildPermitWaitReason);
                blockedCoordinator.MarkReconciled(blocked.Attempt);
            }
            finally
            {
                foreach (var permitLease in permitLeases)
                {
                    permitLease.Dispose();
                }
            }

            var regateCoordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot,
                runInline: true);
            var regated = regateCoordinator.Evaluate(
                candidate,
                ConductorAutonomyPolicy.Conservative,
                (attemptCandidate, _, lease, _) =>
                {
                    Xunit.Assert.NotNull(lease);
                    return ConductorParallelAcceptanceRunResult.Accepted(
                        attemptCandidate,
                        AcceptanceVerificationSummary.PassedWithNoUnmetCriteria);
                });

            Xunit.Assert.Equal(
                ConductorParallelAcceptanceAttemptOutcome.Passed,
                regated.Attempt.Outcome);
            Xunit.Assert.Equal(GoalStatus.Verified, goal.Status);
            Xunit.Assert.True(DotnetBuildEnvironmentManager.IsStableSlotExecutionLeaseAvailable(buildPermit));
        }
        finally
        {
            if (Directory.Exists(attemptRoot))
            {
                Directory.Delete(attemptRoot, recursive: true);
            }
        }
    }

    [Xunit.Fact(DisplayName = "GoalWorktree_concurrent_verified_goals_produce_complete_isolated_partition_sets")]
    public async Task GoalWorktreeConcurrentVerifiedGoalsProduceCompleteIsolatedPartitionSets()
    {
        using var isolatedRoot = new IsolatedDotnetRootScope();
        var kernel = new AgentOrchestratorKernel();
        var goalA = CreateCompletedGoal(kernel, "Verify isolated acceptance A.", Environment.CurrentDirectory);
        var goalAPermit = DotnetBuildEnvironmentManager.CreateAttempt(goalA.Id, "permit-probe").BuildPermitIndex
            ?? throw new InvalidOperationException("Goal A build permit was not assigned.");
        var goalB = CreateCompletedGoal(kernel, "Verify isolated acceptance B.", Environment.CurrentDirectory);
        var goalBPermit = DotnetBuildEnvironmentManager.CreateAttempt(goalB.Id, "permit-probe").BuildPermitIndex
            ?? throw new InvalidOperationException("Goal B build permit was not assigned.");
        for (var attempt = 0;
             attempt < 128 && goalAPermit == goalBPermit;
             attempt++)
        {
            goalB = CreateCompletedGoal(
                kernel,
                $"Verify isolated acceptance B retry {attempt}.",
                Environment.CurrentDirectory);
            goalBPermit = DotnetBuildEnvironmentManager.CreateAttempt(goalB.Id, "permit-probe").BuildPermitIndex
                ?? throw new InvalidOperationException("Goal B build permit was not assigned.");
        }

        Xunit.Assert.NotEqual(goalAPermit, goalBPermit);
        var candidateA = ConductorParallelAcceptanceCandidate.Create(
            goalA,
            0,
            ["src/AcceptanceA.cs"],
            "branch-a",
            "main");
        var candidateB = ConductorParallelAcceptanceCandidate.Create(
            goalB,
            1,
            ["src/AcceptanceB.cs"],
            "branch-b",
            "main");
        var attemptRoot = Path.Combine(Path.GetTempPath(), $"mcg-concurrent-gates-{Guid.NewGuid():N}");
        var bothExecuting = new CountdownEvent(2);
        var release = new ManualResetEventSlim();
        var coverageByGoal = new ConcurrentDictionary<string, TestCoverageInvariantResult>();
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(attemptRoot, runInline: true);

        try
        {
            ConductorParallelAcceptanceRunResult RunPartition(
                ConductorParallelAcceptanceCandidate candidate,
                DotnetBuildEnvironmentLease? lease)
            {
                Xunit.Assert.NotNull(lease);
                bothExecuting.Signal();
                Xunit.Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
                var testName = $"{candidate.GoalPrefix}.PartitionRuns";
                var trx = WritePassingTrx(lease!.Environment.ArtifactsPath, testName);
                var coverage = TestCoverageInvariant.Evaluate(
                    new HashSet<string>([testName], StringComparer.OrdinalIgnoreCase),
                    [new TestPartitionCoverage($"{candidate.GoalPrefix}-partition", true, [trx])]);
                coverageByGoal[candidate.Goal.Id.Value] = coverage;
                return ConductorParallelAcceptanceRunResult.Accepted(
                    candidate,
                    AcceptanceVerificationSummary.PassedWithNoUnmetCriteria);
            }

            var attemptA = Task.Run(() => coordinator.Evaluate(
                candidateA,
                ConductorAutonomyPolicy.Conservative,
                (candidate, _, lease, _) => RunPartition(candidate, lease)));
            var attemptB = Task.Run(() => coordinator.Evaluate(
                candidateB,
                ConductorAutonomyPolicy.Conservative,
                (candidate, _, lease, _) => RunPartition(candidate, lease)));

            Xunit.Assert.True(bothExecuting.Wait(TimeSpan.FromSeconds(5)));
            release.Set();
            var decisions = await Task.WhenAll(attemptA, attemptB);

            Xunit.Assert.All(
                decisions,
                decision => Xunit.Assert.Equal(
                    ConductorParallelAcceptanceAttemptOutcome.Passed,
                    decision.Attempt.Outcome));
            Xunit.Assert.Equal(2, coverageByGoal.Count);
            Xunit.Assert.All(coverageByGoal.Values, coverage =>
            {
                Xunit.Assert.True(coverage.Passed);
                Xunit.Assert.Empty(coverage.EmptyPartitions);
            });
            Xunit.Assert.Equal(
                GoalStatus.Verified,
                goalA.Status);
            Xunit.Assert.Equal(GoalStatus.Verified, goalB.Status);
            Xunit.Assert.True(DotnetBuildEnvironmentManager.IsStableSlotExecutionLeaseAvailable(goalAPermit));
            Xunit.Assert.True(DotnetBuildEnvironmentManager.IsStableSlotExecutionLeaseAvailable(goalBPermit));
        }
        finally
        {
            release.Set();
            if (Directory.Exists(attemptRoot))
            {
                Directory.Delete(attemptRoot, recursive: true);
            }
        }
    }

    private static string WritePassingTrx(string directory, string testName)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}.trx");
        File.WriteAllText(
            path,
            $"""
            <TestRun>
              <TestDefinitions>
                <UnitTest id="1" name="{testName}" />
              </TestDefinitions>
              <Results>
                <UnitTestResult testId="1" testName="{testName}" outcome="Passed" />
              </Results>
            </TestRun>
            """);
        return path;
    }

    private sealed class IsolatedDotnetRootScope : IDisposable
    {
        private readonly string? _previous;
        private readonly string _root;

        public IsolatedDotnetRootScope()
        {
            _root = Path.Combine(
                Path.GetTempPath(),
                $"{DotnetBuildEnvironmentManager.RootDirectoryName}-goal-contention-{Guid.NewGuid():N}");
            _previous = Environment.GetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable);
            Environment.SetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable, _root);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable, _previous);
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
