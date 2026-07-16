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
