using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// A fake child remains live until its host has published its result and returned.
// Track attempt identity so a later launch using the same fixture PID starts live.
internal sealed class AcceptanceHolderExitFixture
{
    private readonly ConcurrentDictionary<string, byte> _exited = new(StringComparer.Ordinal);

    internal void RecordExit(ConductorParallelAcceptanceAttempt attempt) => _exited.TryAdd(attempt.AttemptId, 0);
    internal void RecordExit(ConductorGroupedGateAttempt attempt) => _exited.TryAdd(attempt.AttemptId, 0);

    internal SpawnTrackedProcessStatus Classify(ConductorParallelAcceptanceAttempt attempt) => Classify(attempt.AttemptId);
    internal SpawnTrackedProcessStatus Classify(ConductorGroupedGateAttempt attempt) => Classify(attempt.AttemptId);

    private SpawnTrackedProcessStatus Classify(string attemptId) => _exited.ContainsKey(attemptId)
        ? SpawnTrackedProcessStatus.DeadOrRecycled : SpawnTrackedProcessStatus.LiveMatch;
}
