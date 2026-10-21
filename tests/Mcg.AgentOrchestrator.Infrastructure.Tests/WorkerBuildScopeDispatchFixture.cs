using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Every instance owns its Git repository, worktree, logs and database; no shared mutable environment.
internal sealed class WorkerBuildScopeDispatchFixture : WorkerDispatchTestSupport, IDisposable
{
    internal AgentOrchestratorKernel Kernel { get; set; }
    internal GoalId GoalId { get; }
    internal TaskId PredecessorId { get; }
    internal TaskId SuccessorId { get; }
    internal TaskSpec Predecessor => Kernel.GetTask(GoalId, PredecessorId);
    internal TaskSpec Successor => Kernel.GetTask(GoalId, SuccessorId);
    internal string Main { get; }
    internal string Root { get; }
    internal string IntegrationBranch { get; }
    internal string ReceiptPath { get; }
    internal IClock Clock { get; } = new TestClock(DateTimeOffset.Parse("2026-10-20T12:00:00Z"));
    internal int Builds { get; private set; }

    internal WorkerBuildScopeDispatchFixture()
    {
        Main = CreateSeededDispatchRepository();
        Write(Main, ".gitignore", ".orchestrator-context/\n");
        Write(Main, "Core/Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        Write(Main, "Core/Core.cs", "public class Core { }");
        RunGit(Main, ["add", "-A"], Clock.UtcNow.AddMinutes(-1));
        RunGit(Main, ["commit", "-m", "initial project"], Clock.UtcNow.AddMinutes(-1));
        IntegrationBranch = Git(Main, "branch", "--show-current").Trim();
        Kernel = new AgentOrchestratorKernel(Clock);
        var developer = new TaskSpec(TaskId.New(), "Implement scoped change", AgentRole.Developer);
        var tester = new TaskSpec(TaskId.New(), "Inspect scoped change", AgentRole.Tester);
        var goal = Kernel.CreateGoal("Dispatch-owned build scope", [developer, tester]);
        GoalId = goal.Id;
        PredecessorId = developer.Id;
        SuccessorId = tester.Id;
        Kernel.ActivateGoal(GoalId, new[] { AgentRole.Developer, AgentRole.Tester }.Select(role =>
            new AgentDefinition(new AgentId(role.ToString()), role.ToString(), role,
                new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey))).ToArray());
        Root = GoalWorktrees.Ensure(Main, GoalId);
        Prepare(Predecessor);
        Write(Root, "Core/Core.cs", "public class Core { public int Value => 1; }");
        Commit();
        CompleteProcess(Predecessor);
        ReceiptPath = Path.Combine(Main, "logs", WorkerBuildReceipt.FileName);
        WriteReceipt(Predecessor);
    }

    internal void Prepare(TaskSpec task)
    {
        WorkerBuildScopeDispatchPreparation.Prepare(Kernel.GetGoal(GoalId), task, Root, IntegrationBranch);
        Kernel.RecordTaskDispatch(GoalId, task.Id, new TaskDispatchRecord("codex-cli", "codex exec fixture", Root,
            Clock.UtcNow.AddSeconds(task.RequiredRole == AgentRole.Tester ? -5 : -10), WorkerProviderKind: ProviderKind.OpenAICodexCli));
        WorkerBuildScopeDispatchPreparation.RecordDispatchBaseCommit(Kernel, GoalId, task, Root, Git(Root, "rev-parse", "HEAD").Trim());
    }

    internal void CompleteProcess(TaskSpec task)
    {
        var logs = Path.Combine(Main, "logs");
        Directory.CreateDirectory(logs);
        var stem = Path.Combine(logs, task.Id.Value);
        File.WriteAllText(stem + ".out", WorkerResultBlock("Core/Core.cs", "implemented scoped change", "deferred - acceptance owns tests",
            commit: Git(Root, "rev-parse", "HEAD").Trim()));
        File.WriteAllText(stem + ".err", string.Empty);
        File.WriteAllText(stem + ".exit", "0");
        Kernel.RecordTaskProcessStarted(GoalId, task.Id, new TaskProcessRecord(999999, task.LastDispatch!.Command, Root,
            stem + ".out", stem + ".err", stem + ".exit", task.LastDispatch.DispatchedAt, null, null));
    }

    internal WorkerBuildProjectScope.DispatchContext Context(TaskSpec? task = null)
    {
        task ??= Predecessor;
        return new(GoalId, task.Id, task.LastDispatch!);
    }

    internal WorkerBuildReceiptVerdict Receipt(TaskSpec? task = null) =>
        WorkerBuildReceipt.Evaluate(ReceiptPath, Root, null, Context(task));

    internal OrchestratorBuildEvidenceResolution Resolve(TaskSpec? task = null) =>
        CompletionBuildDispatchReconciliationPolicy.Resolve(task ?? Predecessor, Root, [], [], false,
            () => Receipt(task), _ => { Builds++; return new(false, -1, "No fallback build in controlled fixture."); }, null, GoalId);

    internal void Reconcile(TaskSpec? task = null) => new BackgroundDispatchRunner(Clock,
        isStillRunning: _ => false, findBuildDaemons: _ => [], resolveWorkerBuildReceiptPath: _ => ReceiptPath,
        runOrchestratorBuildCheck: _ => { Builds++; return new(false, -1, "No fallback build in controlled fixture."); })
        .RefreshLatestProcess(Kernel, GoalId, (task ?? Predecessor).Id);

    internal void WriteReceipt(TaskSpec task, IReadOnlyList<string>? projects = null)
    {
        var scope = Context(task).Resolve(Root, []);
        projects ??= scope.Projects;
        Assert.NotEmpty(scope.RequiredProjects);
        Assert.True(WorktreeTreeDigest.TryCompute(Root, out var digest, out var failure), failure);
        var artifacts = Path.Combine(Root, ".orchestrator-context", "build-artifacts");
        Directory.CreateDirectory(artifacts);
        var results = projects.Select((project, index) =>
        {
            var logPath = Path.Combine(artifacts, $"build-{index}.log");
            File.WriteAllText(logPath, "Build succeeded.");
            return new { project, exitCode = 0, logPath };
        }).ToArray();
        File.WriteAllText(ReceiptPath, JsonSerializer.Serialize(new
        {
            schemaVersion = 1, treeDigest = digest, digestAlgorithm = WorktreeTreeDigest.Algorithm,
            buildOutcome = "success", generatedAtUtc = Clock.UtcNow.ToString("O"), worktreeRoot = Root,
            configuration = "Debug", projects, projectResults = results,
            scopeMetadataDigest = scope.MetadataDigest, scopeStableDigest = scope.StableMetadataDigest
        }));
    }

    internal void Commit()
    {
        RunGit(Root, ["add", "-A"], Clock.UtcNow.AddSeconds(-9));
        RunGit(Root, ["commit", "-m", "scoped candidate"], Clock.UtcNow.AddSeconds(-9));
    }

    internal static void Write(string root, string path, string contents)
    {
        var absolute = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        File.WriteAllText(absolute, contents);
    }

    internal static string Git(string root, params string[] arguments)
    {
        var result = GitCli.RunForRepository(root, arguments);
        Assert.True(result.Succeeded && !result.DrainTimedOut, result.Error);
        return result.Output;
    }

    public void Dispose() => ScriptSandboxCleanup.DeleteOrThrow([Root, Main]);
}
