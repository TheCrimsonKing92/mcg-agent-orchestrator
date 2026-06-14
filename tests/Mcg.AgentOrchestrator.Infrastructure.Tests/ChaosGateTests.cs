using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Diagnostics;

/// <summary>
/// Chaos/red-team tests proving each orchestrator safety gate fires under adversarial worker behavior.
/// Uses only fake/scripted runners — no live workers, no network calls, no cost.
/// </summary>
public sealed class ChaosGateTests
{
    private static readonly DateTimeOffset DispatchedAt = DateTimeOffset.Parse("2026-06-02T12:00:00Z");
    private static readonly DateTimeOffset CommittedAt  = DateTimeOffset.Parse("2026-06-02T12:01:00Z");

    // ── Gate 1: False-positive completion rejection ─────────────────────────

    [Xunit.Fact(DisplayName = "ChaosGate1_worker_exits_0_with_no_file_change_is_rejected")]
    public void Gate1_WorkerExitsZeroWithNoFileChange_IsRejected()
    {
        var root = CreateSeededRepo();
        var (kernel, goal, task, _) = CreateChaosDispatch(
            root, AgentRole.Developer,
            WorkerResultBlock("none", "dotnet build", "not run"),
            string.Empty,
            mutateWorktree: null);

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(1, task.LastVerification!.ExitCode);
        Assert.Contains(task.LastVerification.StandardError,
            text => text.Contains("did not produce required relevant file-change evidence", StringComparison.Ordinal));
    }

    // ── Gate 2: Forbidden-changed-paths guard ───────────────────────────────

    [Xunit.Fact(DisplayName = "ChaosGate2_write_to_forbidden_path_blocks_acceptance")]
    public async System.Threading.Tasks.Task Gate2_WriteToForbiddenPath_BlocksAcceptance()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [],
              "forbiddenChangedPathGlobs": [".qwen/**", "bin/**"]
            }
            """);
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([
            new(0, ""),
            new(0, ".qwen/settings.json\nsrc/safe.cs")
        ]);
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            System.Threading.Tasks.Task.FromResult(responses.Dequeue()));

        var result = await verifier.RunAsync(root);

        Assert.False(result.Passed);
        Assert.Equal(1, result.ExitCode);
        var forbidden = result.Checks!.Single(c => c.Name == "forbidden changed paths");
        Assert.False(forbidden.Passed);
        Assert.Contains(forbidden.OutputTail!, text => text.Contains(".qwen/settings.json", StringComparison.Ordinal));
    }

    // ── Anti-tautology: weakening Gate 2 (empty globs) lets write through ──

    [Xunit.Fact(DisplayName = "ChaosGate2_weakened_empty_globs_allow_forbidden_write_proving_gate_is_not_tautological")]
    public async System.Threading.Tasks.Task Gate2_WeakenedEmptyGlobs_AllowForbiddenWrite()
    {
        var root = CreateManifestWorkspace("""
            {
              "version": 1,
              "checks": [],
              "forbiddenChangedPathGlobs": []
            }
            """);
        var responses = new Queue<GoalAcceptanceVerifier.CommandResult>([new(0, "")]);
        var verifier = new GoalAcceptanceVerifier((args, _, _) =>
            System.Threading.Tasks.Task.FromResult(responses.Dequeue()));

        var result = await verifier.RunAsync(root);

        // Weakened gate lets write pass — proves the non-weakened test was non-trivially asserting the gate.
        Assert.True(result.Passed);
        Assert.False(result.Checks!.Any(c => c.Name == "forbidden changed paths" && !c.Passed));
    }

    // ── Gate 3a: Missing WORKER_RESULT → contract validator ─────────────────

    [Xunit.Fact(DisplayName = "ChaosGate3a_missing_worker_result_block_triggers_contract_validator")]
    public void Gate3a_MissingWorkerResultBlock_TriggersContractValidator()
    {
        var root = CreateSeededRepo();
        var (kernel, goal, task, _) = CreateChaosDispatch(
            root, AgentRole.Developer,
            "Done. Files written.",
            string.Empty,
            mutateWorktree: wt => CommitSourceFile(wt, "src/Feature.cs", "// feature"));

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(1, task.LastVerification!.ExitCode);
        Assert.Contains(task.LastVerification.StandardError,
            text => text.Contains("missing WORKER_RESULT block", StringComparison.Ordinal));
    }

    // ── Gate 3b: Malformed WORKER_RESULT (missing skills field) ─────────────

    [Xunit.Fact(DisplayName = "ChaosGate3b_malformed_worker_result_missing_field_triggers_contract_validator")]
    public void Gate3b_MalformedWorkerResultMissingField_TriggersContractValidator()
    {
        var root = CreateSeededRepo();
        const string malformed =
            """
            WORKER_RESULT:
            files: src/Feature.cs
            commands: dotnet build
            tests: Passed
            commit: none
            blockers: none
            model_fit: OpenAI/gpt-5.5 - adequate - chaos test fixture
            END_WORKER_RESULT
            """;
        var (kernel, goal, task, _) = CreateChaosDispatch(
            root, AgentRole.Developer, malformed, string.Empty,
            mutateWorktree: wt => CommitSourceFile(wt, "src/Feature.cs", "// feature"));

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(1, task.LastVerification!.ExitCode);
        Assert.Contains(task.LastVerification.StandardError,
            text => text.Contains("Worker result contract invalid", StringComparison.Ordinal));
    }

    // ── Gate 4: Dirty worktree at completion ─────────────────────────────────

    [Xunit.Fact(DisplayName = "ChaosGate4_dirty_worktree_at_completion_is_rejected")]
    public void Gate4_DirtyWorktreeAtCompletion_IsRejected()
    {
        var root = CreateSeededRepo();
        var (kernel, goal, task, _) = CreateChaosDispatch(
            root, AgentRole.Developer,
            WorkerResultBlock("none", "dotnet build", "not run"),
            string.Empty,
            mutateWorktree: wt => File.WriteAllText(Path.Combine(wt, "Dirty.cs"), "// uncommitted"));

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(1, task.LastVerification!.ExitCode);
        Assert.Contains(task.LastVerification.StandardError,
            text => text.Contains("left the worktree dirty", StringComparison.Ordinal));
    }

    // ── Gate 5: Noise-only commit (.qwen/settings.json) ─────────────────────

    [Xunit.Fact(DisplayName = "ChaosGate5_noise_only_qwen_settings_commit_is_rejected")]
    public void Gate5_NoiseOnlyQwenSettingsCommit_IsRejected()
    {
        var root = CreateSeededRepo();
        var (kernel, goal, task, _) = CreateChaosDispatch(
            root, AgentRole.Developer,
            WorkerResultBlock("none", "none", "not run"),
            string.Empty,
            mutateWorktree: wt =>
            {
                var qwenDir = Path.Combine(wt, ".qwen");
                Directory.CreateDirectory(qwenDir);
                File.WriteAllText(Path.Combine(qwenDir, "settings.json"), "{}");
                RunGit(wt, ["add", "-A"], CommittedAt);
                RunGit(wt, ["commit", "-m", "Qwen settings noise"], CommittedAt);
            });

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(1, task.LastVerification!.ExitCode);
        Assert.Contains(task.LastVerification.StandardError,
            text => text.Contains("did not produce required relevant file-change evidence", StringComparison.Ordinal));
    }

    // ── Gate 6: False 'tests passed' claim with no run ──────────────────────

    [Xunit.Fact(DisplayName = "ChaosGate6_false_tests_passed_claim_without_run_is_rejected")]
    public void Gate6_FalseTestsPassedClaimWithoutRun_IsRejected()
    {
        var root = CreateSeededRepo();
        const string relPath = "src/Mcg.AgentOrchestrator.Core/Application/Feature.cs";
        var (kernel, goal, task, _) = CreateChaosDispatch(
            root, AgentRole.Developer,
            WorkerResultBlock(relPath, "dotnet build", "not run"),
            string.Empty,
            mutateWorktree: wt => CommitSourceFile(wt, relPath, "// core feature"));

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(1, task.LastVerification!.ExitCode);
        Assert.Contains(task.LastVerification.StandardError,
            text => text.Contains("missing required verification policy test evidence", StringComparison.Ordinal));
    }

    // ── Gate 7a: Missing local skill blocks preflight ────────────────────────

    [Xunit.Fact(DisplayName = "ChaosGate7a_missing_local_skill_blocks_subscription_preflight")]
    public void Gate7a_MissingLocalSkill_BlocksSubscriptionPreflight()
    {
        var root = CreateSeededRepo();
        var worktree = CreateLinkedWorktree(root);

        Directory.CreateDirectory(Path.Combine(worktree, ".agents", "skills"));

        var (goal, task, agents) = CreatePreflightScenario(
            "Run dotnet test to verify the implementation.", AgentRole.Developer);

        var result = WorkerProfileDispatcher.PreflightSubscriptionTask(
            goal, task, agents, WorkerProfileCatalog.Default(), worktree, DispatchedAt);

        Assert.False(result.Allowed);
        var findings = string.Join("\n", result.Findings);
        Assert.Contains(findings, text => text.Contains("missing required local skill", StringComparison.Ordinal));
        Assert.Contains(findings, text => text.Contains("dotnet-windows-build-hygiene", StringComparison.Ordinal));
    }

    // ── Gate 7b: Dirty worktree before dispatch blocks preflight ─────────────

    [Xunit.Fact(DisplayName = "ChaosGate7b_dirty_worktree_before_dispatch_blocks_subscription_preflight")]
    public void Gate7b_DirtyWorktreeBeforeDispatch_BlocksSubscriptionPreflight()
    {
        var root = CreateSeededRepo();
        var worktree = GoalWorktrees.Ensure(root, GoalId.New());
        WriteSkill(worktree, "dotnet-windows-build-hygiene");
        File.WriteAllText(Path.Combine(worktree, "Dirty.cs"), "// uncommitted");

        var (goal, task, agents) = CreatePreflightScenario(
            "Run dotnet test to verify the implementation.", AgentRole.Developer);

        var result = WorkerProfileDispatcher.PreflightSubscriptionTask(
            goal, task, agents, WorkerProfileCatalog.Default(), worktree, DispatchedAt);

        Assert.False(result.Allowed);
        var findings = string.Join("\n", result.Findings);
        Assert.Contains(findings, text => text.Contains("uncommitted change", StringComparison.Ordinal));
    }

    // ── Shared setup helpers ─────────────────────────────────────────────────

    private static string CreateSeededRepo()
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

    private static string CreateLinkedWorktree(string root)
    {
        var branch = $"chaos-preflight-{Guid.NewGuid():N}";
        var worktreePath = Path.Combine(root, "chaos-wt");
        RunGit(root, ["worktree", "add", "-b", branch, worktreePath], DispatchedAt.AddMinutes(-1));
        return worktreePath;
    }

    private static void CommitSourceFile(string worktree, string relPath, string content)
    {
        var fullPath = Path.Combine(worktree, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        RunGit(worktree, ["add", "-A"], CommittedAt);
        RunGit(worktree, ["commit", "-m", $"Add {relPath}"], CommittedAt);
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task, TaskProcessRecord Process)
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
            new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey));
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

    private static (Goal Goal, TaskSpec Task, IReadOnlyList<AgentDefinition> Agents) CreatePreflightScenario(
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
            new ModelProfile("OpenAI", "gpt-5.5", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5.5", "low"));
        kernel.ActivateGoal(goal.Id, [agent]);
        var task = goal.Tasks.Single();
        return (goal, task, [agent]);
    }

    private static void RunGit(string workingDirectory, string[] arguments, DateTimeOffset commitTime)
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
        startInfo.Environment["GIT_AUTHOR_DATE"]    = commitTime.ToString("O");
        startInfo.Environment["GIT_COMMITTER_DATE"] = commitTime.ToString("O");
        foreach (var arg in arguments)
            startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git.");
        process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit(60000);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");
    }

    private static string ReadGit(string workingDirectory, string[] arguments)
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
        foreach (var arg in arguments)
            startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git.");
        var output = process.StandardOutput.ReadToEnd();
        var error  = process.StandardError.ReadToEnd();
        process.WaitForExit(60000);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");
        return output.Trim();
    }

    private static string WorkerResultBlock(
        string files,
        string commands,
        string tests,
        string commit   = "{commit}",
        string blockers = "none",
        string modelFit = "OpenAI/gpt-5.5 - adequate - chaos test fixture",
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

    private static void WriteSkill(string workingDirectory, string skillName)
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

    private static string CreateManifestWorkspace(string manifest)
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-chaos-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "config"));
        File.WriteAllText(Path.Combine(root, "config", "acceptance-manifest.json"), manifest);
        return root;
    }
}
