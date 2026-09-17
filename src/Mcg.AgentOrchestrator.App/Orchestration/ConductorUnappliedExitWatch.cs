using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>
/// Counts, per loop, how many consecutive ticks a round has been observed exited without its exit
/// being applied, and emits one operator-visible record when that crosses the threshold.
///
/// Deliberately in-memory and loop-scoped: a fresh conductor generation has not yet had its ticks
/// to apply anything, so a restart correctly resets the count. The record is distinct from the
/// generic sweep escalation so an operator can tell "the conductor has not noticed its own finished
/// work" apart from "a goal is blocked".
/// </summary>
internal sealed class ConductorUnappliedExitWatch
{
    internal const string EventName = "EXIT_UNAPPLIED";

    /// <summary>Ticks a round must be observed unapplied before the record is emitted.</summary>
    internal const int ConsecutiveObservationThreshold = 3;

    private readonly Func<string, bool> _exitArtifactExists;
    private readonly Dictionary<string, int> _consecutiveObservations = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reportedKeys = new(StringComparer.Ordinal);

    internal ConductorUnappliedExitWatch(Func<string, bool>? exitArtifactExists = null)
        => _exitArtifactExists = exitArtifactExists ?? File.Exists;

    /// <summary>
    /// Observes one tick. Returns the records to surface on this tick: at most one per pending round,
    /// emitted only on the observation that reaches the threshold. A key re-arms when its exit is
    /// applied, its process id changes, or its exit artifact path changes.
    /// </summary>
    internal IReadOnlyList<string> Observe(AgentOrchestratorKernel kernel)
    {
        ArgumentNullException.ThrowIfNull(kernel);

        var records = new List<string>();
        var pendingKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var goal in kernel.Goals)
        {
            foreach (var task in goal.Tasks)
            {
                if (task.LastProcess is not { } process ||
                    !DispatchProcessCompletionState.IsExitedWithoutAppliedCompletion(task, process, _exitArtifactExists))
                {
                    continue;
                }

                var key = BuildKey(goal.Id.Value, task.Id.Value, process);
                pendingKeys.Add(key);
                var observations = _consecutiveObservations.GetValueOrDefault(key) + 1;
                _consecutiveObservations[key] = observations;
                if (observations < ConsecutiveObservationThreshold || !_reportedKeys.Add(key))
                {
                    continue;
                }

                records.Add(RenderRecord(goal.Id.Value, task.Id.Value, process, observations));
            }
        }

        foreach (var resolvedKey in _consecutiveObservations.Keys.Except(pendingKeys, StringComparer.Ordinal).ToArray())
        {
            _consecutiveObservations.Remove(resolvedKey);
            _reportedKeys.Remove(resolvedKey);
        }

        return records;
    }

    private static string BuildKey(string goalId, string taskId, TaskProcessRecord process) =>
        $"{goalId}:{taskId}:{process.ProcessId}:{process.ExitCodePath}";

    private static string RenderRecord(string goalId, string taskId, TaskProcessRecord process, int observations) =>
        $"{EventName} goal={Prefix(goalId)} task={Prefix(taskId)} pid={process.ProcessId} " +
        $"artifact={JsonSerializer.Serialize(process.ExitCodePath)} ticks={observations}";

    // Rendering an operator record must never be the thing that throws inside a tick.
    private static string Prefix(string id) => id[..Math.Min(8, id.Length)];
}
