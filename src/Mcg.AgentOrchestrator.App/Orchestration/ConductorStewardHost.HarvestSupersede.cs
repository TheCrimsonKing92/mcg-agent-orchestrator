using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorStewardHost
{
    private bool HarvestTriggerSuperseded(Goal goal, ConductorStewardTrigger trigger)
    {
        if (!_detector.Detect(goal).Any(item =>
                item.Identity == trigger.Identity && item.OccurredAt == trigger.OccurredAt))
            return true;

        // C is anchored to acceptance; F to the answered blocker. Re-detection above checks currency.
        if (trigger.Kind is ConductorStewardTriggerKind.AcceptanceCollectionGuardClass or
            ConductorStewardTriggerKind.ReviewerOrTesterBlockerWithAnswer)
            return false;

        var task = goal.Tasks.FirstOrDefault(item => item.Id.Value == trigger.TaskId);
        if (task is null || task.Status != WorkTaskStatus.Failed ||
            task.LastDispatch is null || task.LastVerification is null)
            return true;

        // DispatchedAt is recorded before the process starts. Identify the paired
        // attempt by the latest dispatch at or before the trigger's start instant.
        var pairedDispatch = task.DispatchHistory
            .Where(item => item.DispatchedAt <= trigger.OccurredAt)
            .MaxBy(item => item.DispatchedAt);
        return pairedDispatch?.DispatchedAt != task.LastDispatch.DispatchedAt ||
               (task.LastVerification.DispatchStartedAt ?? task.LastDispatch.DispatchedAt) != trigger.OccurredAt;
    }

    private static string HarvestVersionDetail(long? claimed, long? harvest) =>
        claimed == harvest ? string.Empty :
        $" claimed_goal_version={claimed?.ToString() ?? "unavailable"}" +
        $" harvest_goal_version={harvest?.ToString() ?? "unavailable"}";
}
