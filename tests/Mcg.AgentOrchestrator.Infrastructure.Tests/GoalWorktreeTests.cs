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

    private protected static string CreateSeededRepository()
    {
        var tempRoot = OperatingSystem.IsWindows()
            ? Path.Combine(FindCurrentSourceRoot(), ".scratch", "mcg-wt")
            : Path.Combine(Path.GetTempPath(), "mcg-worktree-tests");
        var root = Path.Combine(tempRoot, Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        RunGit(root, "init");
        RunGit(root, "config", "user.email", "tests@example.com");
        RunGit(root, "config", "user.name", "Worktree Tests");
        File.WriteAllText(Path.Combine(root, "seed.txt"), "seed");
        RunGit(root, "add", "-A");
        RunGit(root, "commit", "-m", "Seed");
        return root;
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
            AcceptanceVerifier = fakeVerifier
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

        public void AppendGoalLanded(GoalId goalId, string integrationBranch, string goalBranch) { }

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

        public Task<AcceptanceVerificationResult> RunAsync(
            string worktreePath,
            GoalId? goalId = null,
            IReadOnlyList<string>? changedFiles = null,
            int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null,
            CancellationToken cancellationToken = default)
        {
            RunCount++;
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
        var exitCode = RunGitExitCode(workingDirectory, arguments, out var error);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");
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
        return RunGitExitCode(workingDirectory, arguments, out _);
    }

    private protected static int RunGitExitCode(string workingDirectory, string[] arguments, out string error)
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
        process.StandardOutput.ReadToEnd();
        error = process.StandardError.ReadToEnd();
        process.WaitForExit(60000);
        return process.ExitCode;
    }

    private protected static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    private protected static string NormalizePathSeparators(string path) =>
        path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    private protected static void DeleteDirectory(string path)
    {
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

public sealed class GoalWorktreeAcceptanceRetryTests : GoalWorktreeTestBase
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
                ["acceptance-retry", goal.Id.Value[..8], operatorReason, "--confirm-acceptance-retry"],
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

            for (var attempt = 1; attempt <= Goal.OperatorAcceptanceRegateCap; attempt++)
            {
                Assert.True(kernel.BeginGoalAcceptanceVerification(goal.Id, $"Acceptance attempt {attempt}."));
                Assert.True(kernel.ReconcileGoalAcceptanceFailed(
                    goal.Id,
                    ["transient environment failure"],
                    $"Acceptance attempt {attempt} failed."));
                if (attempt == 1)
                {
                    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Cancelled, "Operator deliberately descoped task.");
                }

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
                "Acceptance attempt four failed."));

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
}

public sealed class GoalWorktreeIsolatedDotnetTests : GoalWorktreeTestBase
{
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
                File.Exists(Path.Combine(fixture.DependencyDirectory, "Mcg.AgentOrchestrator.Infrastructure.Tests.deps.json")),
                "The reuse pass must preserve the test assembly dependency directory.");
            Assert.Equal("xUnit executed", File.ReadAllText(fixture.ProbeReceiptPath));

            var log = File.ReadAllText(fixture.DotnetLogPath);
            Assert.DoesNotContain("args=test ", log, StringComparison.Ordinal);
            Assert.DoesNotContain("args=build ", log, StringComparison.Ordinal);
            Assert.Contains("args=build-server shutdown", log, StringComparison.Ordinal);

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
                @".\scripts\Invoke-IsolatedDotnet.ps1 -GoalPrefix reuse-goal build tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj --configuration Debug --verbosity minimal",
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

    private static ReuseFixture CreateReuseFixture(bool includeAssembly)
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-isolated-dotnet-reuse-tests", Guid.NewGuid().ToString("N"));
        var isolatedRoot = Path.Combine(root, "isolated");
        var shimDirectory = Path.Combine(root, "shim");
        var workDirectory = Path.Combine(root, "repo");
        var artifactsPath = Path.Combine(isolatedRoot, "slots", StableSlotName("reuse-goal"), "artifacts");
        const string projectName = "Mcg.AgentOrchestrator.Infrastructure.Tests";
        var dependencyDirectory = Path.Combine(artifactsPath, "bin", projectName, "debug");
        var assemblyPath = Path.Combine(dependencyDirectory, $"{projectName}.dll");
        var executablePath = Path.Combine(dependencyDirectory, $"{projectName}.exe");
        var dotnetLogPath = Path.Combine(root, "dotnet.log");
        var probeReceiptPath = Path.Combine(root, "xunit-probe.txt");

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
        if (includeAssembly)
        {
            CopyDirectory(AppContext.BaseDirectory, dependencyDirectory);
            Assert.True(File.Exists(assemblyPath), $"Current test output is missing {assemblyPath}.");
            Assert.True(File.Exists(executablePath), $"Current test output is missing {executablePath}.");
        }

        File.WriteAllText(
            Path.Combine(shimDirectory, "dotnet.cmd"),
            """
            @echo off
            >> "%DOTNET_SHIM_LOG%" echo args=%*
            exit /b 0
            """);

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
            probeReceiptPath);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunReusePassAsync(ReuseFixture fixture)
    {
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
        startInfo.ArgumentList.Add("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj");
        startInfo.ArgumentList.Add("--no-build");
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add("Debug");
        startInfo.ArgumentList.Add("--verbosity");
        startInfo.ArgumentList.Add("minimal");
        startInfo.ArgumentList.Add("--filter");
        startInfo.ArgumentList.Add("FullyQualifiedName~IsolatedDotnetVSTestBypassProbeTests");
        startInfo.Environment["PATH"] = fixture.ShimDirectory + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty);
        startInfo.Environment["DOTNET_SHIM_LOG"] = fixture.DotnetLogPath;
        startInfo.Environment["MCG_ISOLATED_DOTNET_MTP_PROBE_PATH"] = fixture.ProbeReceiptPath;
        startInfo.Environment[DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable] = fixture.IsolatedRoot;
        startInfo.Environment.Remove(WorkerSandboxOptions.DispatchWorkerVariable);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start PowerShell.");
        process.StandardInput.Close();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
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
            var timedOutStdout = await stdoutTask;
            var timedOutStderr = await stderrTask;
            throw new Xunit.Sdk.XunitException(
                $"Invoke-IsolatedDotnet.ps1 did not exit within 10 seconds.{Environment.NewLine}stdout:{Environment.NewLine}{timedOutStdout}{Environment.NewLine}stderr:{Environment.NewLine}{timedOutStderr}");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return (process.ExitCode, stdout, stderr);
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

    private static string StableSlotName(string value)
    {
        long hash = 0;
        foreach (var character in value.ToLowerInvariant())
        {
            hash = ((hash * 31) + character) % 2147483647;
        }

        return $"slot-{Math.Abs(hash % 4)}";
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
        string ProbeReceiptPath);
}

public sealed class IsolatedDotnetVSTestBypassProbeTests
{
    [Xunit.Fact]
    public void WritesExecutionReceiptWhenRequested()
    {
        var receiptPath = Environment.GetEnvironmentVariable("MCG_ISOLATED_DOTNET_MTP_PROBE_PATH");
        if (!string.IsNullOrWhiteSpace(receiptPath))
        {
            File.WriteAllText(receiptPath, "xUnit executed");
        }
    }
}
