using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Application;

internal static class GoalMonitoringQuery
{
    public static GoalMonitoringBatch BuildBatch(
        AgentOrchestratorKernel kernel,
        Goal goal,
        long sinceEventId,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog workerProfiles,
        OrchestratorWorkspace workspace)
    {
        var events = BuildTimelineEvents(goal);
        var monitor = kernel.BuildMonitor(goal.Id);
        var lifecycle = GoalLifecycle.ResolveState(
            goal,
            GoalMonitoringSubscriptionCommand.ReadLifecycleFacts(workspace, goal));
        var inbox = OperatorInbox.Build(kernel, agents, workerProfiles, workspace, goal.Id.Value[..8], includeAcknowledged: false);
        var capacity = SubscriptionPlanBuilder.Build(goal, agents, workerProfiles).CapacitySchedule;
        var disposition = ConductorOperatorDispositionSnapshots.TryReadLatestForGoal(workspace.RunEventStorePath, goal);
        var snapshot = new GoalMonitoringSnapshot(
            goal.Id.Value,
            DateTimeOffset.UtcNow,
            events.Count,
            new QueryMonitorSnapshot(
                ApplicationGoalStatusText.Resolve(monitor.Status, lifecycle),
                monitor.AttentionItems.Select(_ => new QueryAttention()).ToList()),
            goal.Tasks.Select(task => new TaskMonitoringSnapshot(
                ConsoleViews.GetTaskDisplayNumber(goal, task.Id),
                task.Id.Value,
                task.RequiredRole,
                task.Status,
                ToProcessSnapshot(task.LastProcess),
                task.SubscriptionRetryAfter)).ToList(),
            disposition is null
                ? null
                : new QueryOperatorDisposition(
                    disposition.State,
                    disposition.Confidence,
                    disposition.Reason,
                    disposition.NextSafeCommand,
                    disposition.Blockers),
            new QueryOperatorInbox(inbox.OpenCount),
            new QueryProviderCapacity(capacity.Disposition, capacity.ReadyNowCount, capacity.DeferredCount),
            CliCommandHandlers.ResolveGoalFriendlyLabel(goal, workspace.BacklogStorePath));

        return new GoalMonitoringBatch(
            goal.Id.Value,
            Math.Max(0, sinceEventId),
            events.Count,
            snapshot,
            events.Where(evt => evt.Id > sinceEventId).ToList(),
            $"/api/goals/{goal.Id.Value}/events/stream");
    }

    private static QueryProcessSnapshot? ToProcessSnapshot(TaskProcessRecord? process)
    {
        if (process is null)
            return null;

        var heartbeat = ProcessLogReader.ReadHeartbeat(process);
        return new QueryProcessSnapshot(
            process.ProcessId,
            process.Command,
            process.IsRunning,
            process.StartedAt,
            process.CompletedAt,
            process.ExitCode,
            process.ChildProcessId,
            process.ChildExitCode,
            process.WasCancelled,
            process.StandardOutputPath,
            process.StandardErrorPath,
            process.ExitCodePath,
            heartbeat?.HeartbeatAge?.TotalSeconds,
            heartbeat?.IdleDuration?.TotalSeconds,
            heartbeat?.StandardOutputBytes,
            heartbeat?.StandardErrorBytes,
            heartbeat?.Path,
            new QueryDispatchHeartbeat(
                heartbeat?.IsAvailable ?? false,
                heartbeat?.State ?? "unknown",
                heartbeat?.LastObservedAt));
    }

    private static List<GoalMonitoringEvent> BuildTimelineEvents(Goal goal)
    {
        var taskById = goal.Tasks.ToDictionary(task => task.Id);
        return goal.Timeline
            .Select((evt, index) => new { Event = evt, Index = index })
            .OrderBy(item => item.Event.OccurredAt)
            .ThenBy(item => item.Index)
            .Select((item, index) =>
            {
                TaskSpec? task = null;
                if (item.Event.TaskId is not null)
                    taskById.TryGetValue(item.Event.TaskId, out task);
                var message = OutputTextPreview.CreateTimeline(item.Event.Message);
                return new GoalMonitoringEvent(
                    index + 1,
                    "timeline",
                    goal.Id.Value,
                    item.Event.TaskId?.Value,
                    task is null ? null : ConsoleViews.GetTaskDisplayNumber(goal, task.Id),
                    task?.RequiredRole,
                    task?.Status,
                    item.Event.Kind,
                    message.Text,
                    message.IsTruncated,
                    message.OriginalLength,
                    item.Event.OccurredAt);
            })
            .ToList();
    }
}
