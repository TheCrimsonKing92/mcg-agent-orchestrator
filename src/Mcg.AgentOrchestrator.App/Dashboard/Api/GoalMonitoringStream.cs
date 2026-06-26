using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal static class GoalMonitoringStream
{
    public static GoalMonitoringBatchDto BuildBatch(
        AgentOrchestratorKernel kernel,
        Goal goal,
        long sinceEventId,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog workerProfiles,
        OrchestratorWorkspace workspace)
    {
        var inbox = OperatorInbox.Build(kernel, agents, workerProfiles, workspace, goal.Id.Value[..8], includeAcknowledged: false);
        var subscriptionPlan = SubscriptionPlanBuilder.Build(goal, agents, workerProfiles);
        return DashboardMonitoringEvents.BuildBatch(
            kernel,
            goal,
            sinceEventId,
            DashboardResponseMapper.ToOperatorInboxReportDto(inbox),
            DashboardResponseMapper.ToSubscriptionPlanDto(subscriptionPlan).CapacitySchedule);
    }

    public static async Task StreamAsync(
        Stream stream,
        string goalId,
        Func<CancellationToken, Task<AgentOrchestratorKernel>> loadKernel,
        Func<AgentOrchestratorKernel, Goal, long, GoalMonitoringBatchDto> buildBatch,
        IRunEventStore runEvents,
        long sinceEventId,
        bool once,
        TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        AgentOrchestratorKernel current;
        Goal goal;
        try
        {
            current = await loadKernel(cancellationToken).ConfigureAwait(false);
            goal = OrchestratorEntityResolver.ResolveGoal(current, OrchestratorEntityResolver.GetLatestGoal(current), goalId);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            await WriteGoalNotFoundAsync(stream, goalId, ex, cancellationToken).ConfigureAwait(false);
            return;
        }

        var sinceRunEventSeq = 0L;
        var snapshotWritten = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            var batch = buildBatch(current, goal, sinceEventId);
            if (!snapshotWritten)
            {
                await DashboardMonitoringEvents.WriteServerSentEventAsync(
                    stream,
                    DashboardMonitoringEvents.SnapshotEventName,
                    batch.Snapshot,
                    id: null,
                    cancellationToken).ConfigureAwait(false);
                snapshotWritten = true;
            }

            foreach (var evt in batch.Events)
            {
                await DashboardMonitoringEvents.WriteServerSentEventAsync(
                    stream,
                    evt.Event,
                    evt,
                    DashboardMonitoringEvents.FormatEventId(evt.Id),
                    cancellationToken).ConfigureAwait(false);
                sinceEventId = evt.Id;
            }

            foreach (var status in DashboardMonitoringEvents.BuildTaskStatusEvents(batch))
            {
                await DashboardMonitoringEvents.WriteServerSentEventAsync(
                    stream,
                    DashboardMonitoringEvents.TaskStatusEventName,
                    status,
                    id: $"task-{status.TimelineEventId}",
                    cancellationToken).ConfigureAwait(false);
            }

            foreach (var tick in await ReadConductorTicksAsync(runEvents, sinceRunEventSeq, cancellationToken).ConfigureAwait(false))
            {
                await DashboardMonitoringEvents.WriteServerSentEventAsync(
                    stream,
                    DashboardMonitoringEvents.ConductorTickEventName,
                    tick,
                    id: $"run-{tick.Seq}",
                    cancellationToken).ConfigureAwait(false);
                sinceRunEventSeq = tick.Seq;
                if (tick.ProgressLines is { Count: > 0 })
                {
                    foreach (var line in tick.ProgressLines)
                    {
                        await DashboardMonitoringEvents.WriteServerSentEventAsync(
                            stream,
                            DashboardMonitoringEvents.ConductorProgressEventName,
                            new ConductorProgressLineEventDto(line),
                            id: null,
                            cancellationToken).ConfigureAwait(false);
                    }
                }
            }

            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (once)
            {
                return;
            }

            await DashboardMonitoringEvents.WriteKeepAliveAsync(stream, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
            try
            {
                current = await loadKernel(cancellationToken).ConfigureAwait(false);
                goal = OrchestratorEntityResolver.ResolveGoal(current, OrchestratorEntityResolver.GetLatestGoal(current), goalId);
            }
            catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or ArgumentException)
            {
                await WriteGoalNotFoundAsync(stream, goalId, ex, cancellationToken).ConfigureAwait(false);
                return;
            }
        }
    }

    private static async Task WriteGoalNotFoundAsync(
        Stream stream,
        string goalId,
        Exception ex,
        CancellationToken cancellationToken)
    {
        await DashboardMonitoringEvents.WriteServerSentEventAsync(
            stream,
            DashboardMonitoringEvents.MonitorErrorEventName,
            new MonitorErrorEventDto(goalId, "goal_not_found", ex.Message),
            id: null,
            cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<ConductorTickEvent>> ReadConductorTicksAsync(
        IRunEventStore runEvents,
        long sinceRunEventSeq,
        CancellationToken cancellationToken)
    {
        var records = await runEvents.ReadSinceAsync(sinceRunEventSeq, maxCount: 200, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return DashboardMonitoringEvents.BuildConductorTickEvents(records);
    }
}
