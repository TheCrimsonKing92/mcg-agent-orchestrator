using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public abstract class CliGoalStopAliasTestSupport : CliGoalParkTestSupport
{
    private protected static IReadOnlyList<string> StopParts(ParkSeed seed, string mode,
        bool confirmed = true, string? reasonFile = null)
    {
        var parts = new List<string> { "stop", seed.GoalId.Value[..8] };
        if (reasonFile is null) parts.Add("Operator stop");
        else parts.AddRange(["--text-file", reasonFile]);
        parts.AddRange(["--as", mode]);
        if (confirmed) parts.Add($"--confirm-goal-{mode}");
        return parts;
    }

    private protected static IDisposable RecordTerminations(List<int> calls) => GoalWorkerTermination.Push((snapshot, id) =>
    {
        var kernel = new AgentOrchestratorKernel();
        kernel.ReplaceGoalWithSnapshot(snapshot);
        var process = kernel.GetTask(new GoalId(snapshot.Id), id).LastProcess!;
        calls.Add(process.ProcessId);
        return new GoalWorkerTerminationReceipt(id, process with
        {
            CompletedAt = process.StartedAt.AddSeconds(1), WasCancelled = true
        }, CancellationCandidateEvidence.Indeterminate("recording seam"), ["RESOURCE recording seam"]);
    });

    private protected static Task<long?> Version(ParkSeed seed) =>
        SqliteOrchestratorStateRepository.TryLoadGoalStateVersionAsync(seed.Workspace.SqliteStatePath, seed.GoalId.Value);

    private protected static async Task<CollaborationItemStore> SeedAttention(ParkSeed seed)
    {
        var store = CollaborationItemStore.ForDirectory(seed.Workspace.OrchestratorDirectory);
        await store.RaiseAsync(CollaborationItemType.Decision, seed.GoalId.Value, "Attention", "Keep open until commit");
        return store;
    }
}
