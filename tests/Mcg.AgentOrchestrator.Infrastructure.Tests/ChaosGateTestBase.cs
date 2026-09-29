using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;

/// <summary>
/// Shared helpers for chaos/red-team tests proving each orchestrator safety gate fires under adversarial worker behavior.
/// Uses only fake/scripted runners - no live workers, no network calls, no cost.
/// </summary>
[Xunit.Collection(TestCollections.ChaosGateGit)]
public abstract class ChaosGateTestBase
{
    protected static readonly DateTimeOffset DispatchedAt = DateTimeOffset.Parse("2026-06-02T12:00:00Z");
    protected static readonly DateTimeOffset CommittedAt  = DateTimeOffset.Parse("2026-06-02T12:01:00Z");

    // Shared setup helpers
    protected static string CreateSeededRepo()
    {
        var root = CreateTempDirectory();
        RunGit(root, ["init"], DispatchedAt.AddMinutes(-5));
        RunGit(root, ["config", "user.email", "chaos-tests@example.com"], DispatchedAt.AddMinutes(-5));
        RunGit(root, ["config", "user.name", "Chaos Tests"], DispatchedAt.AddMinutes(-5));
        File.WriteAllText(Path.Combine(root, "seed.txt"), "seed");
        RunGit(root, ["add", "-A"], DispatchedAt.AddMinutes(-5));
        RunGit(root, ["commit", "-m", "Seed"], DispatchedAt.AddMinutes(-5));
        return root;
    }

    protected static string CreateLinkedWorktree(string root)
    {
        var branch = $"chaos-preflight-{Guid.NewGuid():N}";
        var worktreePath = Path.Combine(root, "chaos-wt");
        RunGit(root, ["worktree", "add", "-b", branch, worktreePath], DispatchedAt.AddMinutes(-1));
        return worktreePath;
    }

    protected static void CommitSourceFile(string worktree, string relPath, string content)
    {
        var fullPath = Path.Combine(worktree, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        RunGit(worktree, ["add", "-A"], CommittedAt);
        RunGit(worktree, ["commit", "-m", $"Add {relPath}"], CommittedAt);
    }

    protected static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task, TaskProcessRecord Process)
        CreateChaosDispatch(
            string root,
            AgentRole role,
            string standardOutput,
            string standardError,
            Action<string>? mutateWorktree)
    {
        var kernel = new AgentOrchestratorKernel();
        var taskSpec = new TaskSpec(TaskId.New(), "Chaos gate test task.", role);
        var goal = kernel.CreateGoal("Chaos gate test goal.", [taskSpec]);
        var agent = new AgentDefinition(
            new AgentId(role.ToString().ToLowerInvariant()),
            role.ToString(),
            role,
            new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey));
        kernel.ActivateGoal(goal.Id, [agent]);

        var worktree = GoalWorktrees.Ensure(root, goal.Id);
        mutateWorktree?.Invoke(worktree);

        var head = ReadGit(worktree, ["rev-parse", "--short", "HEAD"]);
        standardOutput = standardOutput.Replace("{commit}", head, StringComparison.Ordinal);

        var logs = Path.Combine(root, "logs");
        Directory.CreateDirectory(logs);
        var stdoutPath = Path.Combine(logs, $"{role}.out.log");
        var stderrPath = Path.Combine(logs, $"{role}.err.log");
        var exitPath   = Path.Combine(logs, $"{role}.exit.txt");
        File.WriteAllText(stdoutPath, standardOutput);
        File.WriteAllText(stderrPath, standardError);
        File.WriteAllText(exitPath, "0");

        var task = goal.Tasks.Single(t => t.RequiredRole == role);
        kernel.RecordTaskDispatch(goal.Id, task.Id,
            new TaskDispatchRecord("codex-cli", "codex exec prompt", worktree, DispatchedAt));
        var process = new TaskProcessRecord(999999, "codex exec prompt", worktree, stdoutPath, stderrPath, exitPath, DispatchedAt, null, null);
        kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
        return (kernel, goal, task, process);
    }

    protected static (Goal Goal, TaskSpec Task, IReadOnlyList<AgentDefinition> Agents) CreatePreflightScenario(
        string taskDescription,
        AgentRole role)
    {
        var kernel = new AgentOrchestratorKernel();
        var taskSpec = new TaskSpec(TaskId.New(), taskDescription, role);
        var goal = kernel.CreateGoal("Chaos preflight goal.", [taskSpec]);
        var agent = new AgentDefinition(
            new AgentId(role.ToString().ToLowerInvariant()),
            role.ToString(),
            role,
            new ModelProfile("OpenAI", AgentCatalog.OpenAiSubscriptionModelAlias, ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("codex-cli", AgentCatalog.OpenAiSubscriptionModelAlias, "low"));
        kernel.ActivateGoal(goal.Id, [agent]);
        var task = goal.Tasks.Single();
        return (goal, task, [agent]);
    }

    protected static void RunGit(string workingDirectory, string[] arguments, DateTimeOffset commitTime)
    {
        var result = RunGitProcess(workingDirectory, arguments, commitTime);
        if (result.ExitCode != 0 && IsGitStdoutWriteTransient(result.Error))
        {
            result = RunGitProcess(workingDirectory, arguments, commitTime);
        }

        if (result.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed with exit {result.ExitCode}: {result.Error}");
    }

    protected static string ReadGit(string workingDirectory, string[] arguments)
    {
        var result = RunGitProcess(workingDirectory, arguments, commitTime: null);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed with exit {result.ExitCode}: {result.Error}");
        return result.Output.Trim();
    }

    private static GitProcessResult RunGitProcess(
        string workingDirectory,
        string[] arguments,
        DateTimeOffset? commitTime)
    {
        var commandEnvironment = new Dictionary<string, string>
        {
            ["GIT_PAGER"] = "cat"
        };
        if (commitTime is not null)
        {
            commandEnvironment["GIT_AUTHOR_DATE"] = commitTime.Value.ToString("O");
            commandEnvironment["GIT_COMMITTER_DATE"] = commitTime.Value.ToString("O");
        }

        var result = InfrastructureTestSupport.RunGitProbe(workingDirectory, arguments, commandEnvironment);
        if (result.TimedOut)
            return new GitProcessResult(-1, string.Empty, $"git {string.Join(' ', arguments)} timed out: {result}");
        return new GitProcessResult(result.ExitCode ?? -1, result.StandardOutput, result.StandardError + "; " + result);
    }

    private static bool IsGitStdoutWriteTransient(string error) =>
        error.Contains("write failure on 'stdout': Bad file descriptor", StringComparison.OrdinalIgnoreCase);

    private readonly record struct GitProcessResult(int ExitCode, string Output, string Error);

    protected static string WorkerResultBlock(
        string files,
        string commands,
        string tests,
        string commit   = "{commit}",
        string blockers = "none",
        string modelFit = "OpenAI/gpt-5.5 - adequate - chaos test fixture", // Deliberate fixture text pins historical/parser behavior independently of the live catalog.
        string skills   = "dotnet-windows-build-hygiene",
        string confidence = "high")
    {
        return $"""
            WORKER_RESULT:
            files: {files}
            commands: {commands}
            tests: {tests}
            commit: {commit}
            blockers: {blockers}
            model_fit: {modelFit}
            skills: {skills}
            confidence: {confidence}
            END_WORKER_RESULT
            """;
    }

    /// <summary>
    /// Produces a WORKER_RESULT block with markdown decoration on the opener and all field keys,
    /// simulating the format that workers using markdown output styles produce.
    /// </summary>
    protected static string MarkdownWorkerResultBlock(
        string files,
        string commands,
        string tests,
        string commit     = "{commit}",
        string blockers   = "none",
        string modelFit   = "OpenAI/gpt-5.5 - adequate - chaos test fixture", // Deliberate fixture text pins historical/parser behavior independently of the live catalog.
        string skills     = "dotnet-windows-build-hygiene",
        string confidence = "high")
    {
        return $"""
            **WORKER_RESULT**:
            **files**: {files}
            **commands**: {commands}
            **tests**: {tests}
            **commit**: {commit}
            **blockers**: {blockers}
            **model_fit**: {modelFit}
            **skills**: {skills}
            **confidence**: {confidence}
            END_WORKER_RESULT
            """;
    }

    protected static void WriteSkill(string workingDirectory, string skillName)
    {
        var dir = Path.Combine(workingDirectory, ".agents", "skills", skillName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"),
            $"""
            ---
            name: {skillName}
            description: Chaos test skill fixture.
            ---
            # {skillName}
            """);
    }

    protected static string CreateManifestWorkspace(string manifest)
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-chaos-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        File.WriteAllText(Path.Combine(root, "config", "acceptance-manifest.json"), manifest);
        return root;
    }
}
