using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class CliCommandTestsPersistentRunnerCommandsGoalIntakeAndReplacement
{
    [Xunit.Fact]
    public void GoalReplaceSuccessorAcceptance_BusyPoolWithoutSelector_ReportsSlotsBusy()
    {
        var root = CreateAcceptanceRepository();
        GoalId? goalId = null;
        IReadOnlyList<DotnetBuildEnvironmentLease> busyHolders = [];
        try
        {
            var workspace = CreateRefinedWorkspace(root);
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Implement replacement successor work", AgentRole.Developer);
            var goal = kernel.CreateGoal("Replacement successor without acceptance slot isolation", [task]);
            goalId = goal.Id;
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            kernel.ActivateGoal(goal.Id, agents);
            kernel.RecordTaskVerification(
                goal.Id,
                task.Id,
                ManualVerificationRecorder.Create(true, "Passed replacement successor fixture.", root, DateTimeOffset.UtcNow));
            Xunit.Assert.Equal(GoalStatus.Verified, goal.Status);
            var worktree = CommitGoalWork(root, goal.Id, "replacement-negative-control.txt", "replacement ready for acceptance");
            Xunit.Assert.Equal(worktree, GoalWorktrees.TryResolve(root, goal.Id));

            var busyRoot = new DotnetBuildStorageRoot(Path.Combine(workspace.OrchestratorDirectory, "replacement-acceptance-busy-pool"));
            busyHolders = AcquireAllStableSlotExecutionLocks(busyRoot);
            Xunit.Assert.All(
                Enumerable.Range(0, DotnetBuildEnvironmentManager.BuildConcurrencySlotCount),
                index => Xunit.Assert.False(DotnetBuildEnvironmentManager.IsStableSlotExecutionLeaseAvailable(index, busyRoot)));

            var verifier = new ProbeAcceptanceVerifier(() => { });
            var context = new CliExecutionContext(
                kernel,
                workspace,
                new InMemoryModelProviderRegistry([]),
                agents,
                WorkerProfileCatalog.Default(),
                goal)
            {
                AcceptanceVerifier = verifier,
                CleanupContext = new WorktreeCleanupContext(
                    GoalWorktreeCleanupOptions.Default,
                    workspace.OrchestratorDirectory,
                    busyRoot),
                StableSlotAcquisitionTimeout = TimeSpan.FromMilliseconds(50)
            };

            var output = CaptureConsole(() => CliCommandHandlers.Execute(
                ["acceptance", goal.Id.Value, "--no-record"],
                context));

            Xunit.Assert.Contains("SLOTS_BUSY", output, StringComparison.Ordinal);
            Xunit.Assert.DoesNotContain("Fast-forwarded", output, StringComparison.Ordinal);
            Xunit.Assert.Equal(0, verifier.RunCount);
            Xunit.Assert.Equal(GoalStatus.Verified, goal.Status);
        }
        finally
        {
            foreach (var holder in busyHolders)
                holder.Dispose();
            CleanupAcceptanceRepository(root, goalId);
        }
    }

    private static IReadOnlyList<DotnetBuildEnvironmentLease> AcquireAllStableSlotExecutionLocks(
        DotnetBuildStorageRoot storageRoot)
    {
        var holders = new List<DotnetBuildEnvironmentLease>();
        try
        {
            for (var index = 0; index < DotnetBuildEnvironmentManager.BuildConcurrencySlotCount; index++)
            {
                holders.Add(DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
                    TimeSpan.FromSeconds(1),
                    storageRoot: storageRoot));
            }

            return holders;
        }
        catch
        {
            foreach (var holder in holders)
                holder.Dispose();
            throw;
        }
    }
}
