using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceStableSlotTestSupport
{
    private static readonly AsyncLocal<SelectionState?> CurrentSelection = new();

    public static int LastSelectionCount => CurrentSelection.Value?.Count ?? 0;

    public static bool ExecuteWithIsolatedStableSlot(
        IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        ref IReadOnlyList<AgentDefinition> agents,
        IModelProviderRegistry providers,
        ref WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal,
        IOperatorChannel? channel = null,
        IGoalAcceptanceVerifier? acceptanceVerifier = null,
        CliPersistentStateRunner.OperatorIntentSubmissionSource operatorIntentSubmissionSource = CliPersistentStateRunner.OperatorIntentSubmissionSource.Cli,
        WorktreeCleanupContext? acceptanceCleanupContext = null)
    {
        var selection = new SelectionState();
        CurrentSelection.Value = selection;
        return CliPersistentStateRunner.ExecuteCommand(
            args,
            stateRepository,
            workspace,
            ref agents,
            providers,
            ref workerProfiles,
            ref currentGoal,
            channel,
            acceptanceVerifier,
            operatorIntentSubmissionSource,
            acceptanceCleanupContext,
            stableSlotSelector: (_, _) =>
            {
                Interlocked.Increment(ref selection.Count);
                return CreateFakeStableSlotLease(workspace.ExecutionDirectory);
            });
    }

    public static DotnetBuildEnvironmentLease CreateFakeStableSlotLease(string root)
    {
        var leaseRoot = Path.Combine(root, ".fake-build-slot");
        Directory.CreateDirectory(leaseRoot);
        var lockPath = Path.Combine(leaseRoot, $"{Guid.NewGuid():N}.lock");
        var stream = new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        var environment = new DotnetBuildEnvironment(
            "fake-acceptance-test-slot",
            leaseRoot,
            Path.Combine(leaseRoot, "artifacts"),
            lockPath,
            [],
            "build-0",
            BuildPermitIndex: 0);
        return new DotnetBuildEnvironmentLease(environment, stream);
    }

    private sealed class SelectionState
    {
        public int Count;
    }
}
