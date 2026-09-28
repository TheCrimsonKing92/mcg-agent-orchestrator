using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private static IReadOnlyList<string> GetRunningAttemptExitCodePaths(
        ConductorDriver driver,
        AgentOrchestratorKernel kernel,
        string? onlyGoalId)
    {
        var goalIds = kernel.Goals
            .Where(goal => (onlyGoalId is null || goal.Id.Value == onlyGoalId) &&
                           goal.Status is not (GoalStatus.Completed or GoalStatus.Cancelled or GoalStatus.Failed or GoalStatus.Superseded))
            .Select(goal => goal.Id.Value)
            .ToArray();

        var paths = new List<string>();
        foreach (var coordinator in new[]
                 {
                     driver.FocusedEvidenceAttemptCoordinator,
                     driver.ParallelAcceptanceAttemptCoordinator
                 })
        {
            try
            {
                paths.AddRange(coordinator.GetRunningAttemptExitCodePaths(goalIds));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
            {
                // An unreadable attempt directory cannot prevent dispatch and stop-file wakeups.
            }
        }

        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static string FormatWatchWake(int tick, string stopFilePath, IConductorWakeSignal? wakeSignal)
    {
        var reason = IsStopRequested(stopFilePath)
            ? ConductorWakeReason.StopFile
            : (wakeSignal as IConductorAttemptExitWakeSignal)?.LastWakeReason ?? ConductorWakeReason.Unknown;
        var token = reason switch
        {
            ConductorWakeReason.AttemptExit => "attempt-exit",
            ConductorWakeReason.DispatchExit => "dispatch-exit",
            ConductorWakeReason.OperatorIntent => "operator-intent",
            ConductorWakeReason.StopFile => "stop-file",
            _ => "unknown"
        };
        return $"WATCH_WAKE tick={tick} reason={token}";
    }
}
