using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.CostControl;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Text.Json;

public abstract class CliCommandTestBase
{
    private protected static OrchestratorWorkspace CreateRefinedWorkspace(string root)
    {
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(
                ModelFunctionPurposes.SpecRefiner,
                ModelLane.CheapApi,
                new ModelProfile("missing-provider", "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey),
                Name: ModelFunctionPurposes.SpecRefiner)
        ]));
        return workspace;
    }

    private protected static IDisposable ClearWorkerSandboxEnv()
    {
        var previous = Environment.GetEnvironmentVariable(WorkerSandboxOptions.EnabledVariable);
        Environment.SetEnvironmentVariable(WorkerSandboxOptions.EnabledVariable, null);
        return new WorkerSandboxEnvRestore(previous);
    }

    private protected sealed class WorkerSandboxEnvRestore(string? previous) : IDisposable
    {
        public void Dispose()
        {
            Environment.SetEnvironmentVariable(WorkerSandboxOptions.EnabledVariable, previous);
        }
    }

    private protected static BacklogItem BacklogItemFor(
        string title,
        string body = "",
        BacklogItemStatus status = BacklogItemStatus.Open,
        DateTimeOffset? updatedAt = null) =>
        new(Guid.NewGuid().ToString("n"), title, body, status, DateTimeOffset.UtcNow, updatedAt ?? DateTimeOffset.UtcNow, null);

    private protected static string ExecuteCliAndCapture(
        IReadOnlyList<string> parts,
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;

        return CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            parts,
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));
    }

    private protected static void AssertHelpCommandDoesNotResolveGoal(IReadOnlyList<string> parts, string expectedUsage)
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var beforeFiles = SnapshotFiles(root);
        var changed = true;

        var output = CaptureConsole(() => changed = CliCommandDispatcher.ExecuteCommand(
            parts,
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        Xunit.Assert.False(changed);
        Xunit.Assert.Contains(expectedUsage, output);
        Xunit.Assert.Contains("-h, --help", output);
        Xunit.Assert.Empty(kernel.Goals);
        Xunit.Assert.Null(currentGoal);
        Xunit.Assert.Equal(beforeFiles, SnapshotFiles(root));
    }

    private protected static string[] SnapshotFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    private protected static CliProcessResult RunAppCommand(string workingDirectory, params string[] arguments)
    {
        var appAssembly = Path.Combine(AppContext.BaseDirectory, "Mcg.AgentOrchestrator.App.dll");
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };
        startInfo.EnvironmentVariables[OrchestratorWorkspace.RepoRootEnvironmentVariable] = workingDirectory;
        startInfo.EnvironmentVariables["OLLAMA_BASE_URL"] = "http://127.0.0.1:1";
        startInfo.ArgumentList.Add(appAssembly);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start app process.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(60000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("App command did not exit within 60 seconds.");
        }

        return new CliProcessResult(process.ExitCode, stdout, stderr);
    }

    private protected sealed record CliProcessResult(int ExitCode, string Stdout, string Stderr);

    private protected static void WritePlanningBacklog(string root)
    {
        SeedBacklog(root, """
        # Backlog

        ## Decision record (durable context, not work items)

        Not work.

        ## Add feature A planner

        Implement feature A planning in docs/feature-a.md. Done when focused documentation checks pass.

        ## Add feature B planner

        Implement feature B planning in docs/feature-b.md. Done when focused documentation checks pass.

        ## Add feature C planner

        Implement feature C planning in docs/feature-c.md. Done when focused documentation checks pass.
        """);
    }

    private protected static void WriteCompilationBacklog(string root)
    {
        SeedBacklog(root, """
        # Backlog

        ## Add feature A compiler support

        Implement deterministic feature A planning in src/FeatureA/Planner.cs with tests in tests/FeatureA.Tests/PlannerTests.cs. Done when focused planner tests pass.

        ## Add feature B compiler report

        Implement feature B reporting in src/FeatureB/Report.cs with tests in tests/FeatureB.Tests/ReportTests.cs. Done when focused report tests pass.
        """);
    }

    private protected static void WriteConflictingCompilationBacklog(string root)
    {
        SeedBacklog(root, """
        # Backlog

        ## Add feature A compiler support

        Implement deterministic feature A planning in src/FeatureA/Planner.cs. Done when focused planner tests pass.

        ## Refine feature A compiler support

        Update edge handling in src/FeatureA/Planner.cs. Done when focused planner tests pass.
        """);
    }

    private protected static void MakeLeaseOwnerStale(string metadataPath)
    {
        var text = File.ReadAllText(metadataPath);
        File.WriteAllText(metadataPath, text.Replace(
            $"\"ownerProcessId\": {Environment.ProcessId}",
            "\"ownerProcessId\": 999999",
            StringComparison.Ordinal));
    }

    private protected static AgentDefinition SubscriptionPlanner(string id, string name) => new(
        new AgentId(id),
        name,
        AgentRole.Planner,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile(id));

    private protected static AgentDefinition SubscriptionDeveloper() => new(
        new AgentId("developer"),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile("codex-cli"));

    private protected static void DirtyGoalWorktree(string root, Goal goal)
    {
        EnsureGitRepository(root);
        var worktree = GoalWorktrees.Ensure(root, goal.Id);
        Directory.CreateDirectory(Path.Combine(worktree, "src"));
        File.WriteAllText(Path.Combine(worktree, "src", "dirty.txt"), "dirty");
    }

    private protected static void EnsureGitRepository(string root)
    {
        RunGit(root, "init");
        RunGit(root, "config", "user.email", "tests@example.invalid");
        RunGit(root, "config", "user.name", "Tests");
        File.WriteAllText(Path.Combine(root, "README.md"), "test repo");
        RunGit(root, "add", "README.md");
        RunGit(root, "commit", "-m", "init");
    }

    private protected static string[] ReadyBlockedLines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("READY_BLOCKED ", StringComparison.Ordinal))
            .ToArray();

    private protected static int CountLinesContaining(string text, string value) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Count(line => line.Contains(value, StringComparison.Ordinal));

    private protected static int CountNonEmptyLines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Length;

    private protected static string SingleLineContaining(string text, string value) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.Contains(value, StringComparison.Ordinal));

    private protected static AgentDefinition TestAgent(string id, AgentRole role) => new(
        new AgentId(id),
        id,
        role,
        new ModelProfile("Fake", id, ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.ApiOnly);

    private protected static string AssignedAgentId(Goal goal, AgentRole role)
    {
        return goal.Tasks.Single(task => task.RequiredRole == role).AssignedAgentId!.Value;
    }

    private protected static void RecordRunningProcess(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        string workingDirectory,
        int processId = 999999)
    {
        var stdout = Path.Combine(workingDirectory, $"{task.Id.Value}-out.log");
        var stderr = Path.Combine(workingDirectory, $"{task.Id.Value}-err.log");
        var exit = Path.Combine(workingDirectory, $"{task.Id.Value}-exit.txt");
        File.WriteAllText(stdout, string.Empty);
        File.WriteAllText(stderr, string.Empty);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord("codex-cli", "codex exec prompt.md", workingDirectory, DateTimeOffset.UtcNow));
        kernel.RecordTaskProcessStarted(
            goal.Id,
            task.Id,
            new TaskProcessRecord(processId, "codex exec prompt.md", workingDirectory, stdout, stderr, exit, DateTimeOffset.UtcNow, null, null));
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

    private protected static string CreateAcceptanceRepository()
    {
        var root = CreateTempDirectory();
        RunGit(root, "init", "-b", "main");
        RunGit(root, "config", "user.email", "tests@example.com");
        RunGit(root, "config", "user.name", "CLI Tests");
        File.WriteAllText(Path.Combine(root, "DOGFOOD_LOG.md"), "# Dogfood Log" + Environment.NewLine);
        File.WriteAllText(Path.Combine(root, "seed.txt"), "seed");
        RunGit(root, "add", "-A");
        RunGit(root, "commit", "-m", "Seed");
        return root;
    }

    private protected static string CreateShortAcceptanceRepository()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "mcg-short-tests");
        Directory.CreateDirectory(baseDirectory);
        var root = Path.Combine(baseDirectory, Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(root);
        RunGit(root, "init", "-b", "main");
        RunGit(root, "config", "user.email", "tests@example.com");
        RunGit(root, "config", "user.name", "CLI Tests");
        File.WriteAllText(Path.Combine(root, "DOGFOOD_LOG.md"), "# Dogfood Log" + Environment.NewLine);
        File.WriteAllText(Path.Combine(root, "seed.txt"), "seed");
        RunGit(root, "add", "-A");
        RunGit(root, "commit", "-m", "Seed");
        return root;
    }

    private protected static string CommitGoalWork(string root, GoalId goalId, string relativePath, string content)
    {
        var worktree = GoalWorktrees.Ensure(root, goalId);
        var path = Path.Combine(worktree, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        RunGit(worktree, "add", "-A");
        RunGit(worktree, "commit", "-m", "Goal work");
        return worktree;
    }

    private protected static void CleanupAcceptanceRepository(string root, GoalId? goalId)
    {
        try
        {
            if (goalId is not null && Directory.Exists(root))
            {
                _ = GoalWorktrees.Remove(root, goalId);
            }
        }
        catch
        {
            // Best-effort test cleanup.
        }

        try
        {
            if (Directory.Exists(root))
            {
                _ = GoalWorktrees.DeleteDirectory(root);
            }
        }
        catch
        {
            // Temp directories are pruned by the OS.
        }
    }

    private protected static string BuildTaskStatusProjectionJson(GoalSnapshot snapshot)
    {
        var landingRelevantState = new
        {
            Tasks = snapshot.Tasks
                .OrderBy(task => task.Id, StringComparer.Ordinal)
                .Select(task => new
                {
                    task.Id,
                    Role = task.RequiredRole,
                    task.Status
                })
        };

        return JsonSerializer.Serialize(landingRelevantState);
    }

    private protected static (int ExitCode, string StandardOutput, string StandardError) RunAppCli(
        string workingDirectory,
        IReadOnlyList<string> args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.EnvironmentVariables[OrchestratorWorkspace.RepoRootEnvironmentVariable] = workingDirectory;
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Mcg.AgentOrchestrator.App.dll"));
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start app CLI.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"CLI did not exit for: {string.Join(' ', args)}");
        }

        return (process.ExitCode, output, error);
    }

    private protected static async Task<AppCliTimeoutResult> RunAppCliWithExitTimeout(
        string workingDirectory,
        IReadOnlyList<string> args,
        TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.EnvironmentVariables[OrchestratorWorkspace.RepoRootEnvironmentVariable] = workingDirectory;
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Mcg.AgentOrchestrator.App.dll"));
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start app CLI.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        var exited = process.WaitForExit((int)timeout.TotalMilliseconds);
        if (!exited)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        }

        return new AppCliTimeoutResult(
            exited,
            exited ? process.ExitCode : null,
            await output,
            await error);
    }

    private protected sealed record AppCliTimeoutResult(
        bool ExitedWithinTimeout,
        int? ExitCode,
        string StandardOutput,
        string StandardError);

    private protected static void RunGit(string workingDirectory, params string[] arguments)
    {
        _ = RunGitOutput(workingDirectory, arguments);
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

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(60000);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");
        }

        return output;
    }

    private protected sealed class InMemoryTransactionalStateRepository : ITransactionalOrchestratorStateRepository
    {
        private AgentOrchestratorKernel _kernel;

        public InMemoryTransactionalStateRepository(AgentOrchestratorKernel kernel)
        {
            _kernel = Clone(kernel);
        }

        public int TransactionCount { get; private set; }

        public bool IsInTransaction { get; private set; }

        public int LoadCount { get; private set; }

        public int LoadGoalsCount { get; private set; }

        public int LoadGoalCount { get; private set; }

        public int SaveGoalSnapshotsCount { get; private set; }

        public List<string> LoadedGoalIds { get; } = [];

        public List<IReadOnlyList<string>> LoadGoalBatches { get; } = [];

        public HashSet<string> CleanedUpGoalIds { get; } = new(StringComparer.Ordinal);

        public Action<AgentOrchestratorKernel>? BeforeNextTransaction { get; set; }

        public Action<CancellationToken>? BeforeSaveCommit { get; set; }

        public Task<AgentOrchestratorKernel> LoadAsync(CancellationToken cancellationToken = default)
        {
            LoadCount++;
            return Task.FromResult(Clone(_kernel));
        }

        public Task<AgentOrchestratorKernel> LoadGoalsAsync(
            IReadOnlyCollection<GoalId> goalIds,
            CancellationToken cancellationToken = default)
        {
            LoadGoalsCount++;
            LoadGoalBatches.Add(goalIds.Select(id => id.Value).OrderBy(id => id, StringComparer.Ordinal).ToArray());
            var snapshot = _kernel.ExportSnapshot();
            var filtered = snapshot.Goals
                .Where(goal => goalIds.Any(id => id.Value == goal.Id))
                .ToList();
            LoadedGoalIds.AddRange(filtered.Select(goal => goal.Id));
            return Task.FromResult(AgentOrchestratorKernel.FromSnapshot(snapshot with { Goals = filtered }));
        }

        public Task SaveAsync(AgentOrchestratorKernel kernel, CancellationToken cancellationToken = default)
        {
            _kernel = Clone(kernel);
            return Task.CompletedTask;
        }

        public Task SaveGoalSnapshotsAsync(
            IReadOnlyCollection<GoalSnapshot> goals,
            CancellationToken cancellationToken = default)
        {
            SaveGoalSnapshotsCount++;
            if (goals.Count == 0)
                return Task.CompletedTask;

            var replacements = goals.ToDictionary(goal => goal.Id, StringComparer.Ordinal);
            var snapshot = _kernel.ExportSnapshot();
            var merged = snapshot.Goals
                .Select(goal => replacements.TryGetValue(goal.Id, out var replacement) ? replacement : goal)
                .ToList();
            merged.AddRange(replacements.Values.Where(goal => snapshot.Goals.All(existing => existing.Id != goal.Id)));
            _kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with { Goals = merged });
            return Task.CompletedTask;
        }

        public async Task<IReadOnlyList<GoalSnapshotSaveResult>> SaveGoalSnapshotsWithMergeAsync(
            IReadOnlyCollection<GoalSnapshotSaveRequest> goals,
            CancellationToken cancellationToken = default)
        {
            var snapshots = goals.Select(goal => goal.Current).ToArray();
            await SaveGoalSnapshotsAsync(snapshots, cancellationToken);
            return snapshots
                .Select(snapshot => new GoalSnapshotSaveResult(
                    snapshot.Id,
                    GoalSnapshotSaveDisposition.Saved,
                    snapshot,
                    "in-memory save"))
                .ToArray();
        }

        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GoalSummary>>(_kernel.Goals
                .Select(goal => new GoalSummary(
                    goal.Id.Value,
                    CleanedUpGoalIds.Contains(goal.Id.Value) ? "CleanedUp" : goal.Status.ToString(),
                    goal.Objective,
                    DateTimeOffset.UtcNow.ToString("O")))
                .ToList());

        public Task<IReadOnlyList<GoalSummary>> ListConductLoopGoalMetadataAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GoalSummary>>(_kernel.Goals
                .Where(goal => !CleanedUpGoalIds.Contains(goal.Id.Value))
                .Select(goal => new GoalSummary(
                    goal.Id.Value,
                    CleanedUpGoalIds.Contains(goal.Id.Value) ? "CleanedUp" : goal.Status.ToString(),
                    goal.Objective,
                    DateTimeOffset.UtcNow.ToString("O")))
                .ToList());

        public Task<IReadOnlyList<ModelFitHistoryRow>> ListModelFitHistoryAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ModelFitHistoryRow>>([]);

        public Task<IReadOnlyList<ModelOutcomeRecord>> BuildModelOutcomeScorecardAsync(
            int windowSize = ModelOutcomeScorecard.DefaultWindowSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ModelOutcomeRecord>>([]);

        public Task<ModelFitBestFit?> QueryBestFitForRoleAsync(AgentRole role, CancellationToken cancellationToken = default) =>
            Task.FromResult<ModelFitBestFit?>(null);

        public Task<T> TransactAsync<T>(
            Func<AgentOrchestratorKernel, CancellationToken, Task<(bool ShouldSave, T Result)>> transaction,
            CancellationToken cancellationToken = default) =>
            TransactAsync(async (kernel, _, token) => await transaction(kernel, token), cancellationToken);

        public async Task<T> TransactAsync<T>(
            Func<AgentOrchestratorKernel, Func<Task>, CancellationToken, Task<(bool ShouldSave, T Result)>> transaction,
            CancellationToken cancellationToken = default)
        {
            TransactionCount++;
            if (BeforeNextTransaction is { } before)
            {
                BeforeNextTransaction = null;
                before(_kernel);
            }

            var transactionKernel = Clone(_kernel);
            Task CheckpointAsync()
            {
                _kernel = Clone(transactionKernel);
                return Task.CompletedTask;
            }

            IsInTransaction = true;
            try
            {
                var (shouldSave, result) = await transaction(transactionKernel, CheckpointAsync, cancellationToken);
                if (shouldSave)
                {
                    BeforeSaveCommit?.Invoke(cancellationToken);
                    _kernel = Clone(transactionKernel);
                }

                return result;
            }
            finally
            {
                IsInTransaction = false;
            }
        }

        public Task<GoalSnapshot?> LoadGoalAsync(GoalId goalId, CancellationToken cancellationToken = default)
        {
            LoadGoalCount++;
            LoadedGoalIds.Add(goalId.Value);
            var snap = _kernel.ExportSnapshot().Goals.FirstOrDefault(g => g.Id == goalId.Value);
            return Task.FromResult<GoalSnapshot?>(snap);
        }

        public async Task<T> TransactGoalAsync<T>(
            GoalId goalId,
            Func<GoalSnapshot?, CancellationToken, Task<(bool ShouldSave, GoalSnapshot? NewSnapshot, T Result)>> transaction,
            CancellationToken cancellationToken = default)
        {
            TransactionCount++;
            if (BeforeNextTransaction is { } before)
            {
                BeforeNextTransaction = null;
                before(_kernel);
            }

            var snap = _kernel.ExportSnapshot().Goals.FirstOrDefault(g => g.Id == goalId.Value);
            IsInTransaction = true;
            try
            {
                var (shouldSave, newSnapshot, result) = await transaction(snap, cancellationToken);
                if (shouldSave && newSnapshot is not null)
                {
                    await SaveGoalSnapshotsAsync([newSnapshot], cancellationToken);
                }

                return result;
            }
            finally
            {
                IsInTransaction = false;
            }
        }

        private static AgentOrchestratorKernel Clone(AgentOrchestratorKernel kernel) =>
            AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
    }

    private protected sealed class ProbeAcceptanceVerifier : IGoalAcceptanceVerifier
    {
        private readonly Action<DotnetBuildEnvironmentLease?> _onRun;

        public ProbeAcceptanceVerifier(Action onRun)
            : this(_ => onRun())
        {
        }

        public ProbeAcceptanceVerifier(Action<DotnetBuildEnvironmentLease?> onRun)
        {
            _onRun = onRun;
        }

        public int RunCount { get; private set; }

        public int? LastStableSlotIndex { get; private set; }

        public Task<AcceptanceVerificationResult> RunAsync(
            string worktreePath,
            GoalId? goalId = null,
            IReadOnlyList<string>? changedFiles = null,
            int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null,
            CancellationToken cancellationToken = default)
        {
            RunCount++;
            LastStableSlotIndex = stableSlotIndex;
            LastStableSlotLease = stableSlotLease;
            _onRun(stableSlotLease);
            return Task.FromResult(new AcceptanceVerificationResult(
                true,
                false,
                0,
                "Passed.",
                ArtifactsPath: Path.Combine(worktreePath, "artifacts"),
                Checks: [new AcceptanceCheckResult("probe verifier", true, 0, "Passed.", DurationMilliseconds: 7)]));
        }

        public DotnetBuildEnvironmentLease? LastStableSlotLease { get; private set; }
    }

}

