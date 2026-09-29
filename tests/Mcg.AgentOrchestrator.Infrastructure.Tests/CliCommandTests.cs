using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.CostControl;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

public abstract class CliCommandTestBase : HostCapacityBoundTestBase
{
    private protected static WorktreeCleanupContext CreateIsolatedCleanupContext(
        OrchestratorWorkspace workspace,
        GoalWorktreeCleanupHooks? hooks = null)
    {
        var root = new DotnetBuildStorageRoot(Path.Combine(workspace.ExecutionDirectory, ".orchestrator", "test-dotnet"));
        return hooks is null
            ? WorktreeCleanupContext.Load(attentionStoreDirectory: workspace.OrchestratorDirectory, buildStorageRoot: root)
            : new WorktreeCleanupContext(hooks with { BuildStorageRoot = root });
    }

    private protected static OrchestratorWorkspace CreateRefinedWorkspace(string root)
    {
        SeedLocalSkillCatalog(root);
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        _ = StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
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
        OrchestratorWorkspace workspace,
        TextReader? standardInput = null,
        bool? isStandardInputRedirected = null)
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
            ref currentGoal,
            standardInput: standardInput,
            isStandardInputRedirected: isStandardInputRedirected));
    }

    private protected static (string Output, bool Changed, Goal? CurrentGoal) ExecuteCliAndCaptureResult(
        IReadOnlyList<string> parts,
        AgentOrchestratorKernel kernel,
        OrchestratorWorkspace workspace)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var providers = new InMemoryModelProviderRegistry([]);
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        var changed = false;

        var output = CaptureConsole(() => changed = CliCommandDispatcher.ExecuteCommand(
            parts,
            kernel,
            workspace,
            ref agents,
            providers,
            ref profiles,
            ref currentGoal));

        return (output, changed, currentGoal);
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

    private protected static string NormalizeVolatileTimes(GoalSnapshot snapshot)
    {
        var node = JsonSerializer.SerializeToNode(snapshot)!;
        NormalizeVolatileTimes(node);
        return node.ToJsonString();
    }

    private static void NormalizeVolatileTimes(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj.ToArray())
                {
                    if (property.Value is null)
                        continue;

                    if (property.Key is "OccurredAt" or "CompletedAt" or "LatestRetryAt" or "RecordedAt")
                    {
                        obj[property.Key] = "<time>";
                    }
                    else
                    {
                        NormalizeVolatileTimes(property.Value);
                    }
                }
                break;

            case JsonArray array:
                foreach (var item in array)
                {
                    NormalizeVolatileTimes(item);
                }
                break;
        }
    }

    private protected static CliProcessResult RunAppCommand(string workingDirectory, params string[] arguments)
    {
        var appAssembly = Path.Combine(AppContext.BaseDirectory, "Mcg.AgentOrchestrator.App.dll");
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };
        startInfo.EnvironmentVariables[OrchestratorWorkspace.RepoRootEnvironmentVariable] = workingDirectory;
        startInfo.EnvironmentVariables[DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable] =
            Path.Combine(workingDirectory, ".orchestrator", "test-dotnet");
        startInfo.EnvironmentVariables["OLLAMA_BASE_URL"] = "http://127.0.0.1:1";
        startInfo.ArgumentList.Add(appAssembly);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start app process.");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("App command did not exit within 60 seconds.");
        }

        return new CliProcessResult(process.ExitCode, stdout.GetAwaiter().GetResult(),
            stderr.GetAwaiter().GetResult());
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
        new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey),
        ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
        Subscription: new SubscriptionLaunchProfile(id));

    private protected static AgentDefinition SubscriptionDeveloper() => new(
        new AgentId("developer"),
        "Developer",
        AgentRole.Developer,
        new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey),
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

    private protected static void WriteIdentityBoundHeartbeat(TaskProcessRecord process, int processId)
    {
        var identity = DispatchProcessIdentityEvidence.ReadCurrent(processId)
            ?? throw new InvalidOperationException($"Could not read process identity for PID {processId}.");
        var observedAt = DateTimeOffset.UtcNow;
        File.WriteAllText(
            BackgroundDispatchRunner.GetHeartbeatPath(process),
            JsonSerializer.Serialize(new
            {
                pid = processId,
                childPid = (int?)null,
                ownedPids = new[] { processId },
                ownedProcessIdentities = new[]
                {
                    new
                    {
                        processId = identity.ProcessId,
                        startedAt = identity.StartedAt,
                        imagePath = identity.ImagePath
                    }
                },
                state = "running",
                lastObservedAt = observedAt,
                lastProgressAt = observedAt,
                stdoutBytes = 0,
                stderrBytes = 0,
                ownedCpuMs = 0
            }));
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
                _ = GoalWorktrees.DeleteDirectoryWithRetry(root);
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
        startInfo.EnvironmentVariables[DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable] =
            Path.Combine(workingDirectory, ".orchestrator", "test-dotnet");
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
        startInfo.EnvironmentVariables[DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable] =
            Path.Combine(workingDirectory, ".orchestrator", "test-dotnet");
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
        var result = InfrastructureTestSupport.RunGitProbe(workingDirectory, arguments);
        InfrastructureTestSupport.RequireCompleteGitOutput(result);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed with exit {result.ExitCode}: {result.StandardError}; {result}");
        }

        return result.StandardOutput;
    }

    private protected sealed class InMemoryTransactionalStateRepository : IOrchestratorStateOutboxRepository
    {
        private AgentOrchestratorKernel _kernel;
        private readonly Dictionary<string, OrchestratorStateOutboxMessage> _outbox = new(StringComparer.Ordinal);
        private readonly HashSet<string> _outboxClaims = new(StringComparer.Ordinal);

        public InMemoryTransactionalStateRepository(AgentOrchestratorKernel kernel)
        {
            _kernel = Clone(kernel);
        }

        public int TransactionCount { get; private set; }

        public int TransactAsyncCount { get; private set; }

        public int TransactGoalCount { get; private set; }

        public bool IsInTransaction { get; private set; }

        public int LoadCount { get; private set; }

        public int LoadGoalsCount { get; private set; }

        public int LoadGoalCount { get; private set; }

        public int SaveGoalSnapshotsCount { get; private set; }

        public int SaveAsyncCount { get; private set; }

        public int ListOutboxMessagesCount { get; private set; }

        public int CompletedHumanInputQueryCount { get; private set; }

        public long LoadedGoalSnapshotJsonBytes { get; private set; }

        public int LoadWhileInTransactionCount { get; private set; }

        public int TransactGoalDelegateCalls { get; private set; }

        public int LastSavedGoalStateHumanInputCount { get; private set; }

        public IReadOnlyCollection<GoalSnapshotSaveRequest>? LastGoalSnapshotSaveRequests { get; private set; }

        public Func<IReadOnlyCollection<GoalSnapshotSaveRequest>, IReadOnlyList<GoalSnapshotSaveResult>>?
            GoalSnapshotSaveResultFactory { get; set; }

        public List<string> LoadedGoalIds { get; } = [];

        public List<IReadOnlyList<string>> LoadGoalBatches { get; } = [];

        public HashSet<string> CleanedUpGoalIds { get; } = new(StringComparer.Ordinal);

        public Action<AgentOrchestratorKernel>? BeforeNextTransaction { get; set; }

        public Action<AgentOrchestratorKernel>? BeforeNextLoadAsync { get; set; }

        public Action<AgentOrchestratorKernel>? BeforeNextLoadGoalAsync { get; set; }

        public Action<CancellationToken>? BeforeSaveCommit { get; set; }

        public Action<AgentOrchestratorKernel>? BeforeGoalCasRetry { get; set; }

        public bool ThrowOnLoadAsync { get; set; }

        public bool ThrowOnLoadWhileInTransaction { get; set; }

        public Task<AgentOrchestratorKernel> LoadAsync(CancellationToken cancellationToken = default)
        {
            if (ThrowOnLoadAsync)
            {
                throw new InvalidOperationException("LoadAsync is not allowed for this test.");
            }

            if (BeforeNextLoadAsync is { } before)
            {
                BeforeNextLoadAsync = null;
                before(_kernel);
            }

            RecordLoadBoundary();
            LoadCount++;
            return Task.FromResult(Clone(_kernel));
        }

        public Task<AgentOrchestratorKernel> LoadGoalsAsync(
            IReadOnlyCollection<GoalId> goalIds,
            CancellationToken cancellationToken = default)
        {
            RecordLoadBoundary();
            LoadGoalsCount++;
            LoadGoalBatches.Add(goalIds.Select(id => id.Value).OrderBy(id => id, StringComparer.Ordinal).ToArray());
            var snapshot = _kernel.ExportSnapshot();
            var filtered = snapshot.Goals
                .Where(goal => goalIds.Any(id => id.Value == goal.Id))
                .ToList();
            var filteredHumanInput = snapshot.HumanInputRequests
                .Where(request => goalIds.Any(id => id.Value == request.GoalId))
                .ToList();
            LoadedGoalIds.AddRange(filtered.Select(goal => goal.Id));
            LoadedGoalSnapshotJsonBytes += filtered.Sum(MeasureGoalSnapshotJsonBytes);
            return Task.FromResult(AgentOrchestratorKernel.FromSnapshot(snapshot with
            {
                Goals = filtered,
                HumanInputRequests = filteredHumanInput
            }));
        }

        public long EstimateGoalSnapshotJsonBytes(IReadOnlyCollection<GoalId> goalIds)
        {
            var ids = goalIds.Select(id => id.Value).ToHashSet(StringComparer.Ordinal);
            return _kernel.ExportSnapshot().Goals
                .Where(goal => ids.Contains(goal.Id))
                .Sum(MeasureGoalSnapshotJsonBytes);
        }

        public Task SaveAsync(AgentOrchestratorKernel kernel, CancellationToken cancellationToken = default)
        {
            SaveAsyncCount++;
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
            LastGoalSnapshotSaveRequests = goals.ToArray();
            if (GoalSnapshotSaveResultFactory is not null)
                return GoalSnapshotSaveResultFactory(goals);

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
                .Select(goal => new GoalSummary(
                    goal.Id.Value,
                    CleanedUpGoalIds.Contains(goal.Id.Value) ? "CleanedUp" : goal.Status.ToString(),
                    goal.Objective,
                    DateTimeOffset.UtcNow.ToString("O")))
                .ToList());

        public Task<IReadOnlyList<GoalId>> ListGoalIdsWithCompletedHumanInputAsync(
            IReadOnlyCollection<GoalId> goalIds,
            CancellationToken cancellationToken = default)
        {
            CompletedHumanInputQueryCount++;
            var ids = goalIds.Select(id => id.Value).ToHashSet(StringComparer.Ordinal);
            return Task.FromResult<IReadOnlyList<GoalId>>(_kernel.ExportSnapshot().HumanInputRequests
                .Where(request =>
                    ids.Contains(request.GoalId) &&
                    request.IsCompleted &&
                    !IsSyntheticParkedHumanWaitCompletion(request))
                .Select(request => new GoalId(request.GoalId))
                .Distinct()
                .ToArray());
        }

        private static bool IsSyntheticParkedHumanWaitCompletion(HumanInputRequestSnapshot request) =>
            !request.WasDismissed &&
            request.Answer?.StartsWith("Goal parked:", StringComparison.OrdinalIgnoreCase) == true;

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
            TransactAsyncCount++;
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

        public async Task<T> TransactWithOutboxAsync<T>(
            Func<AgentOrchestratorKernel, CancellationToken, Task<(
                bool ShouldSave,
                T Result,
                IReadOnlyList<OrchestratorStateOutboxMessage> OutboxMessages)>> transaction,
            CancellationToken cancellationToken = default)
        {
            TransactionCount++;
            TransactAsyncCount++;
            if (BeforeNextTransaction is { } before)
            {
                BeforeNextTransaction = null;
                before(_kernel);
            }

            var transactionKernel = Clone(_kernel);
            IsInTransaction = true;
            try
            {
                var (shouldSave, result, messages) = await transaction(transactionKernel, cancellationToken);
                BeforeSaveCommit?.Invoke(cancellationToken);
                if (shouldSave)
                    _kernel = Clone(transactionKernel);
                foreach (var message in messages)
                    _outbox[message.Id] = message;
                return result;
            }
            finally
            {
                IsInTransaction = false;
            }
        }

        public Task<IReadOnlyList<OrchestratorStateOutboxMessage>> ListOutboxMessagesAsync(
            string kind,
            CancellationToken cancellationToken = default)
        {
            ListOutboxMessagesCount++;
            return Task.FromResult<IReadOnlyList<OrchestratorStateOutboxMessage>>(_outbox.Values
                .Where(message => message.Kind.Equals(kind, StringComparison.Ordinal))
                .OrderBy(message => message.CreatedAt)
                .ThenBy(message => message.Id, StringComparer.Ordinal)
                .ToArray());
        }

        public async Task<bool> TryProcessOutboxMessageAsync(
            string id,
            Func<OrchestratorStateOutboxMessage, CancellationToken, Task<OrchestratorStateOutboxProcessingResult>> processor,
            CancellationToken cancellationToken = default)
        {
            TransactionCount++;
            TransactAsyncCount++;
            IsInTransaction = true;
            OrchestratorStateOutboxMessage? message;
            try
            {
                lock (_outbox)
                {
                    if (!_outbox.TryGetValue(id, out message) || !_outboxClaims.Add(id))
                        return false;
                }
            }
            finally
            {
                IsInTransaction = false;
            }

            try
            {
                _ = await processor(message, cancellationToken);
                TransactionCount++;
                TransactAsyncCount++;
                IsInTransaction = true;
                lock (_outbox)
                {
                    if (!_outboxClaims.Remove(id) || !_outbox.Remove(id))
                        throw new InvalidOperationException($"Outbox message '{id}' lost its processing claim before finalization.");
                }
                return true;
            }
            catch
            {
                lock (_outbox)
                    _outboxClaims.Remove(id);
                throw;
            }
            finally
            {
                IsInTransaction = false;
            }
        }

        public Task<GoalSnapshot?> LoadGoalAsync(GoalId goalId, CancellationToken cancellationToken = default)
        {
            if (BeforeNextLoadGoalAsync is { } before)
            {
                BeforeNextLoadGoalAsync = null;
                before(_kernel);
            }

            RecordLoadBoundary();
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
            TransactGoalCount++;
            if (BeforeNextTransaction is { } before)
            {
                BeforeNextTransaction = null;
                before(_kernel);
            }

            var snap = _kernel.ExportSnapshot().Goals.FirstOrDefault(g => g.Id == goalId.Value);
            TransactGoalDelegateCalls++;
            var (shouldSave, newSnapshot, result) = await transaction(snap, cancellationToken);
            if (shouldSave && newSnapshot is not null && BeforeGoalCasRetry is { } beforeRetry)
            {
                BeforeGoalCasRetry = null;
                beforeRetry(_kernel);
                snap = _kernel.ExportSnapshot().Goals.FirstOrDefault(g => g.Id == goalId.Value);
                TransactGoalDelegateCalls++;
                (shouldSave, newSnapshot, result) = await transaction(snap, cancellationToken);
            }

            if (shouldSave && newSnapshot is not null)
            {
                await SaveGoalSnapshotsAsync([newSnapshot], cancellationToken);
            }

            return result;
        }

        public async Task<T> TransactGoalStateAsync<T>(
            GoalId goalId,
            Func<GoalStateSnapshot?, CancellationToken, Task<(bool ShouldSave, GoalStateSnapshot? NewState, T Result)>> transaction,
            CancellationToken cancellationToken = default)
        {
            TransactionCount++;
            TransactGoalCount++;
            if (BeforeNextTransaction is { } before)
            {
                BeforeNextTransaction = null;
                before(_kernel);
            }

            GoalStateSnapshot? LoadState()
            {
                var snapshot = _kernel.ExportSnapshot();
                var goal = snapshot.Goals.FirstOrDefault(candidate => candidate.Id == goalId.Value);
                if (goal is null)
                    return null;

                var humanInputRequests = snapshot.HumanInputRequests
                    .Where(request => request.GoalId == goalId.Value)
                    .ToArray();
                return new GoalStateSnapshot(goal, humanInputRequests);
            }

            var state = LoadState();
            TransactGoalDelegateCalls++;
            var (shouldSave, newState, result) = await transaction(state, cancellationToken);
            if (shouldSave && newState is not null && BeforeGoalCasRetry is { } beforeRetry)
            {
                BeforeGoalCasRetry = null;
                beforeRetry(_kernel);
                state = LoadState();
                TransactGoalDelegateCalls++;
                (shouldSave, newState, result) = await transaction(state, cancellationToken);
            }

            if (shouldSave && newState is not null)
            {
                SaveGoalSnapshotsCount++;
                LastSavedGoalStateHumanInputCount = newState.HumanInputRequests.Count;
                var snapshot = _kernel.ExportSnapshot();
                var goals = snapshot.Goals
                    .Select(goal => goal.Id == goalId.Value ? newState.Goal : goal)
                    .ToList();
                var humanInputRequests = snapshot.HumanInputRequests
                    .Where(request => request.GoalId != goalId.Value)
                    .Concat(newState.HumanInputRequests)
                    .ToArray();
                _kernel = AgentOrchestratorKernel.FromSnapshot(
                    snapshot with { Goals = goals, HumanInputRequests = humanInputRequests });
            }

            return result;
        }

        private static AgentOrchestratorKernel Clone(AgentOrchestratorKernel kernel) =>
            AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());

        private static long MeasureGoalSnapshotJsonBytes(GoalSnapshot goal) =>
            JsonSerializer.SerializeToUtf8Bytes(goal).LongLength;

        private void RecordLoadBoundary()
        {
            if (!IsInTransaction)
                return;

            LoadWhileInTransactionCount++;
            if (ThrowOnLoadWhileInTransaction)
            {
                throw new InvalidOperationException("Loading state while a transaction is active is not allowed for this test.");
            }
        }
    }

    private protected sealed class ProbeAcceptanceVerifier : IGoalAcceptanceVerifier
    {
        private readonly Action<DotnetBuildEnvironmentLease?> _onRun;
        private readonly Func<string, AcceptanceVerificationResult>? _resultFactory;

        public ProbeAcceptanceVerifier(Action onRun)
            : this(_ => onRun())
        {
        }

        public ProbeAcceptanceVerifier(Action<DotnetBuildEnvironmentLease?> onRun)
            : this(onRun, null)
        {
        }

        public ProbeAcceptanceVerifier(AcceptanceVerificationResult result)
            : this(_ => { }, _ => result)
        {
        }

        private ProbeAcceptanceVerifier(
            Action<DotnetBuildEnvironmentLease?> onRun,
            Func<string, AcceptanceVerificationResult>? resultFactory)
        {
            _onRun = onRun;
            _resultFactory = resultFactory;
        }

        public int RunCount { get; private set; }

        public int? LastStableSlotIndex { get; private set; }

        public Task<AcceptanceVerificationResult> RunOwnedAsync(
            string worktreePath,
            GoalId? goalId,
            IReadOnlyList<string>? changedFiles,
            int? stableSlotIndex,
            DotnetBuildEnvironmentLease? stableSlotLease,
            IAcceptanceAttemptExecutionOwner executionOwner)
        {
            RunCount++;
            LastStableSlotIndex = stableSlotIndex;
            LastStableSlotLease = stableSlotLease;
            _onRun(stableSlotLease);
            if (_resultFactory is not null)
            {
                return Task.FromResult(_resultFactory(worktreePath));
            }

            return Task.FromResult(new AcceptanceVerificationResult(
                true,
                false,
                0,
                "Passed.",
                ArtifactsPath: Path.Combine(worktreePath, "artifacts"),
                Checks: [new AcceptanceCheckResult("probe verifier", true, 0, "Passed.", DurationMilliseconds: 7)]));
        }

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
                Summary: "probe focused evidence passed",
                Checks: [new AcceptanceCheckResult("probe focused evidence", true, 0, null, ArtifactsPath: Path.Combine(worktreePath, "artifacts"))]));

        public DotnetBuildEnvironmentLease? LastStableSlotLease { get; private set; }
    }

}

