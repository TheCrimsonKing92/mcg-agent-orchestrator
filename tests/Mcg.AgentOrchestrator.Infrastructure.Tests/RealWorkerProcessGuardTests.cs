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

    [Xunit.Fact(DisplayName = "Guard ignores scoped real worker outside test host process tree")]
    public void GuardIgnoresScopedRealWorkerOutsideTestHostProcessTree()
    {
        const int ownerProcessId = 100;
        const int ownedHarmlessProcessId = 101;
        const int unrelatedWorkerProcessId = 300;
        var ownerStartedAt = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
        var snapshot = new ProcessCommandLineSnapshot(new Dictionary<int, ProcessInspectionRecord>
        {
            [ownerProcessId] = AvailableProcess(
                ownerProcessId,
                parentProcessId: 50,
                ownerStartedAt,
                "dotnet Mcg.AgentOrchestrator.Infrastructure.Tests.dll"),
            [ownedHarmlessProcessId] = AvailableProcess(
                ownedHarmlessProcessId,
                ownerProcessId,
                ownerStartedAt.AddSeconds(1),
                "pwsh -Command Write-Output C:\\temp\\mcg-orchestrator-tests\\fixture"),
            [unrelatedWorkerProcessId] = AvailableProcess(
                unrelatedWorkerProcessId,
                parentProcessId: 200,
                ownerStartedAt.AddSeconds(2),
                "pwsh -Command codex exec --cd C:\\temp\\mcg-tests\\p640\\.orchestrator-worktrees\\unrelated")
        });

        var matches = RealWorkerProcessGuard.FindCurrentMatches(
            ownerProcessId,
            snapshot,
            ["C:\\temp\\mcg-tests\\p64"],
            out var commandLineEnumerationAvailable);

        Assert.True(commandLineEnumerationAvailable);
        Assert.Empty(matches);
    }

    [Xunit.Fact(DisplayName = "Guard discovers current test host process temp ownership token")]
    public void GuardDiscoversCurrentTestHostProcessTempOwnershipToken()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var currentTempRoot = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        var ownedRoots = RealWorkerProcessGuard.FindOwnedProcessTempRoots(Environment.ProcessId);

        Assert.Contains(
            ownedRoots,
            root => string.Equals(root, currentTempRoot, StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact(DisplayName = "Guard retains scoped real worker detection for test host descendant")]
    public void GuardRetainsScopedRealWorkerDetectionForTestHostDescendant()
    {
        const int ownerProcessId = 100;
        const int ownedWorkerProcessId = 101;
        var ownerStartedAt = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
        const string workerCommand =
            "pwsh -Command codex exec --cd C:\\temp\\mcg-orchestrator-tests\\fixture\\.orchestrator-worktrees\\goal";
        var snapshot = new ProcessCommandLineSnapshot(new Dictionary<int, ProcessInspectionRecord>
        {
            [ownerProcessId] = AvailableProcess(
                ownerProcessId,
                parentProcessId: 50,
                ownerStartedAt,
                "dotnet Mcg.AgentOrchestrator.Infrastructure.Tests.dll"),
            [ownedWorkerProcessId] = AvailableProcess(
                ownedWorkerProcessId,
                ownerProcessId,
                ownerStartedAt.AddSeconds(1),
                workerCommand)
        });

        var match = Assert.Single(RealWorkerProcessGuard.FindCurrentMatches(
            ownerProcessId,
            snapshot,
            ownedProcessTempRoots: [],
            out var commandLineEnumerationAvailable));

        Assert.True(commandLineEnumerationAvailable);
        Assert.Equal(ownedWorkerProcessId, match.ProcessId);
        Assert.Equal(workerCommand, match.CommandLine);
    }

    [Xunit.Fact(DisplayName = "Guard retains orphaned worker through exact process temp root")]
    public void GuardRetainsOrphanedWorkerThroughExactProcessTempRoot()
    {
        const int ownerProcessId = 100;
        const int orphanedWorkerProcessId = 101;
        const string ownedRoot = "C:\\temp\\mcg-tests\\p64";
        var ownerStartedAt = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
        const string workerCommand =
            "pwsh -Command claude -p --cd C:\\temp\\mcg-tests\\p64\\fixture\\.orchestrator-worktrees\\goal";
        var snapshot = new ProcessCommandLineSnapshot(new Dictionary<int, ProcessInspectionRecord>
        {
            [ownerProcessId] = AvailableProcess(
                ownerProcessId,
                parentProcessId: 50,
                ownerStartedAt,
                "dotnet Mcg.AgentOrchestrator.Infrastructure.Tests.dll"),
            [orphanedWorkerProcessId] = AvailableProcess(
                orphanedWorkerProcessId,
                parentProcessId: 1,
                ownerStartedAt.AddSeconds(1),
                workerCommand)
        });

        var match = Assert.Single(RealWorkerProcessGuard.FindCurrentMatches(
            ownerProcessId,
            snapshot,
            [ownedRoot],
            out var commandLineEnumerationAvailable));

        Assert.True(commandLineEnumerationAvailable);
        Assert.Equal(orphanedWorkerProcessId, match.ProcessId);
    }

    [Xunit.Fact(DisplayName = "Guard retains descendant below unreadable intermediate process")]
    public void GuardRetainsDescendantBelowUnreadableIntermediateProcess()
    {
        const int ownerProcessId = 100;
        const int unreadableProcessId = 101;
        const int ownedWorkerProcessId = 102;
        var ownerStartedAt = DateTimeOffset.Parse("2026-09-01T12:00:00Z");
        const string workerCommand =
            "pwsh -Command codex exec --cd C:\\temp\\mcg-orchestrator-tests\\fixture\\.orchestrator-worktrees\\goal";
        var snapshot = new ProcessCommandLineSnapshot(new Dictionary<int, ProcessInspectionRecord>
        {
            [ownerProcessId] = AvailableProcess(
                ownerProcessId,
                parentProcessId: 50,
                ownerStartedAt,
                "dotnet Mcg.AgentOrchestrator.Infrastructure.Tests.dll"),
            [unreadableProcessId] = new ProcessInspectionRecord(
                unreadableProcessId,
                ownerProcessId,
                "pwsh",
                ExecutablePath: null,
                StartedAt: null,
                CommandLine: null,
                ProcessInspectionStatus.AccessDenied),
            [ownedWorkerProcessId] = AvailableProcess(
                ownedWorkerProcessId,
                unreadableProcessId,
                ownerStartedAt.AddSeconds(2),
                workerCommand)
        });

        var match = Assert.Single(RealWorkerProcessGuard.FindCurrentMatches(
            ownerProcessId,
            snapshot,
            ownedProcessTempRoots: [],
            out var commandLineEnumerationAvailable));

        Assert.True(commandLineEnumerationAvailable);
        Assert.Equal(ownedWorkerProcessId, match.ProcessId);
    }

    [Xunit.Fact(DisplayName = "Advance subscription start does not spawn scoped real worker process")]
    public void AdvanceSubscriptionStartDoesNotSpawnScopedRealWorkerProcess()
    {
        var baseline = RealWorkerProcessGuard.CaptureSnapshot();
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
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
            var result = new GoalAdvancementOperations().AdvanceGoalWithSubscriptionsUntilBlocked(
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
        var workspace = CreateRefinedWorkspace(root);
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

    private static ProcessInspectionRecord AvailableProcess(
        int processId,
        int parentProcessId,
        DateTimeOffset startedAt,
        string commandLine) =>
        new(
            processId,
            parentProcessId,
            "pwsh",
            ExecutablePath: null,
            startedAt,
            commandLine,
            ProcessInspectionStatus.Available);

    private static string EnsureGoalWorktree(string root, GoalId goalId)
    {
        SeedLocalSkillCatalog(root);
        if (!Directory.Exists(Path.Combine(root, ".git")))
        {
            AssertGit(root, "init", "-b", "main");
            AssertGit(root, "config", "user.email", "tests@example.com");
            AssertGit(root, "config", "user.name", "Real Worker Process Guard Tests");
            File.WriteAllText(Path.Combine(root, "seed.txt"), "seed");
            AssertGit(root, "add", "-A");
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
        public Task<AcceptanceVerificationResult> RunOwnedAsync(
            string worktreePath,
            GoalId? goalId,
            IReadOnlyList<string>? changedFiles,
            int? stableSlotIndex,
            DotnetBuildEnvironmentLease? stableSlotLease,
            IAcceptanceAttemptExecutionOwner executionOwner) =>
            Task.FromResult(new AcceptanceVerificationResult(true, false, 0, "ok"));

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
                Summary: "focused evidence passed",
                Checks: []));
    }
}
