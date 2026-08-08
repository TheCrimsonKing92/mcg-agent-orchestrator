using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.EnvMutation)]
public sealed class RealWorkerProcessGuardTests
{
    [Xunit.Fact(DisplayName = "Real worker rewrite is inert when disabled")]
    public void RealWorkerRewriteIsInertWhenDisabled()
    {
        const string command = "codex exec --model gpt-5-codex --cd C:\\temp\\mcg-orchestrator-tests\\abc";

        var rewritten = BackgroundDispatchRunner.RewriteRealWorkerCommandForTests(command, rewriteEnabled: false);

        Assert.Equal(command, rewritten);
    }

    [Xunit.Fact(DisplayName = "Real worker rewrite leaves harmless local command unchanged")]
    public void RealWorkerRewriteLeavesHarmlessLocalCommandUnchanged()
    {
        const string command = "Write-Output {promptPath}";

        var rewritten = BackgroundDispatchRunner.RewriteRealWorkerCommandForTests(command, rewriteEnabled: true);

        Assert.Equal(command, rewritten);
    }

    [Xunit.Fact(DisplayName = "Real worker rewrite replaces codex subscription command")]
    public void RealWorkerRewriteReplacesCodexSubscriptionCommand()
    {
        const string command = "codex exec --model gpt-5.3-codex-spark --cd C:\\temp\\mcg-orchestrator-tests\\abc";

        var rewritten = BackgroundDispatchRunner.RewriteRealWorkerCommandForTests(command, rewriteEnabled: true);

        Assert.NotEqual(command, rewritten);
        Assert.DoesNotContain("codex exec", rewritten, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WORKER_RESULT:", rewritten, StringComparison.Ordinal);
    }

    [Xunit.Fact(DisplayName = "Advance subscription start does not spawn scoped real worker process")]
    public void AdvanceSubscriptionStartDoesNotSpawnScopedRealWorkerProcess()
    {
        var baseline = RealWorkerProcessGuard.CaptureSnapshot();
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var objective = "Design and implement a production multi-tenant distributed architecture " + new string('o', 5000);
        var task = new TaskSpec(
            TaskId.New(),
            "Build an end-to-end distributed integration with API CLI dashboard provider subscription worker persistence state tests docs " + new string('t', 5000),
            AgentRole.Developer,
            "Verify the full integration with build, tests, dashboard smoke, and focused regression evidence. " + new string('v', 5000));
        var goal = CreateRefinedGoal(kernel, objective, [task]);
        var agent = new AgentDefinition(
            new AgentId("developer"),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "gpt-5-codex", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5-codex"));
        kernel.ActivateGoal(goal.Id, [agent]);
        EnsureGoalWorktree(root, goal.Id);

        try
        {
            var result = GoalManagementCommandService.AdvanceGoalWithSubscriptionsUntilBlocked(
                kernel,
                [agent],
                WorkerProfileCatalog.Default(),
                workspace,
                goal);

            Assert.True(result.Executed);
            Assert.True(result.StepCount > 0);
            Assert.NotNull(task.LastDispatch);
            Assert.Contains("codex exec", task.LastDispatch.Command, StringComparison.OrdinalIgnoreCase);

            WaitForDispatchHostExit(task.LastProcess?.ProcessId);
            RealWorkerProcessGuard.AssertNoNewMatches(baseline);
        }
        finally
        {
            TryKillDispatchHost(task.LastProcess?.ProcessId);
        }
    }

    [Xunit.Fact(DisplayName = "Conductor default subscription start does not spawn scoped real worker process")]
    public void ConductorDefaultSubscriptionStartDoesNotSpawnScopedRealWorkerProcess()
    {
        var baseline = RealWorkerProcessGuard.CaptureSnapshot();
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var objective = "Implement a focused source change through the default conductor dispatch path.";
        var task = new TaskSpec(
            TaskId.New(),
            "Implement the default conductor dispatch path regression fixture.",
            AgentRole.Developer,
            "Verify the default conductor dispatch path regression fixture.");
        var goal = CreateRefinedGoal(kernel, objective, [task]);
        var agent = new AgentDefinition(
            new AgentId("developer"),
            "Developer",
            AgentRole.Developer,
            new ModelProfile("OpenAI", "gpt-5.3-codex-spark", ModelCapability.Text, SubscriptionMode.ApiKey),
            ExecutionPolicy: AgentExecutionPolicy.SubscriptionOnly,
            Subscription: new SubscriptionLaunchProfile("codex-cli", "gpt-5.3-codex-spark"));
        kernel.ActivateGoal(goal.Id, [agent]);
        EnsureGoalWorktree(root, goal.Id);

        var driver = new ConductorDriver(
            kernel,
            workspace,
            new PassingAcceptanceVerifier(),
            [agent],
            WorkerProfileCatalog.Default());

        try
        {
            var result = driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive);

            var executed = Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
            Assert.Equal(GoalLifecycleState.WorkspaceReady, executed.FromState);
            Assert.NotNull(task.LastDispatch);
            Assert.Contains("codex exec", task.LastDispatch.Command, StringComparison.OrdinalIgnoreCase);

            WaitForDispatchHostExit(task.LastProcess?.ProcessId);
            RealWorkerProcessGuard.AssertNoNewMatches(baseline);
        }
        finally
        {
            TryKillDispatchHost(task.LastProcess?.ProcessId);
        }
    }

    private static Goal CreateRefinedGoal(AgentOrchestratorKernel kernel, string objective, IReadOnlyList<TaskSpec> tasks)
    {
        var goal = kernel.CreateGoal(objective, tasks);
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Real worker process guard fixture goal is already refined.",
            ["Real worker process guard fixture goal is already refined."],
            VerificationClass.TestVerifiable,
            [],
            []));
        return goal;
    }

    private static string EnsureGoalWorktree(string root, GoalId goalId)
    {
        if (!Directory.Exists(Path.Combine(root, ".git")))
        {
            AssertGit(root, "init", "-b", "main");
            AssertGit(root, "config", "user.email", "tests@example.com");
            AssertGit(root, "config", "user.name", "Real Worker Process Guard Tests");
            File.WriteAllText(Path.Combine(root, "seed.txt"), "seed");
            AssertGit(root, "add", "seed.txt");
            AssertGit(root, "commit", "-m", "Seed");
        }

        return GoalWorktrees.Ensure(root, goalId);
    }

    private static void AssertGit(string workingDirectory, params string[] args)
    {
        var result = GitCli.Run(workingDirectory, args);
        Assert.True(result.Succeeded, $"git {string.Join(' ', args)} failed: {result.Error}");
    }

    private static void WaitForDispatchHostExit(int? processId)
    {
        if (processId is null)
        {
            return;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId.Value);
            process.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
        }
    }

    private static void TryKillDispatchHost(int? processId)
    {
        if (processId is null)
        {
            return;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId.Value);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private sealed class PassingAcceptanceVerifier : IGoalAcceptanceVerifier
    {
        public Task<AcceptanceVerificationResult> RunAsync(
            string worktreePath,
            GoalId? goalId = null,
            IReadOnlyList<string>? changedFiles = null,
            int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new AcceptanceVerificationResult(true, false, 0, "ok"));

        public Task<FocusedEvidenceRunResult> RunFocusedEvidenceAsync(
            string worktreePath,
            GoalId? goalId,
            string request,
            int? stableSlotIndex = null,
            DotnetBuildEnvironmentLease? stableSlotLease = null,
            bool runBaselineArm = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new FocusedEvidenceRunResult(
                request,
                Accepted: true,
                Passed: true,
                Summary: "focused evidence passed",
                Checks: []));
    }
}
