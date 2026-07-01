using System.Globalization;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal static class DashboardMonitoringEvents
{
    public const string SnapshotEventName = "goal.snapshot";
    public const string MonitorErrorEventName = "monitor.error";
    public const string KeepAliveEventName = "monitor.keepalive";
    public const string TaskStatusEventName = "task.status";
    public const string ConductorTickEventName = "conductor.tick";
    public const string ConductorProgressEventName = "conductor.progress";
    private static readonly JsonSerializerOptions CaseInsensitiveJson = new() { PropertyNameCaseInsensitive = true };

    public static string StreamPath(string goalId) => $"/api/goals/{goalId}/events/stream";

    public static GoalMonitoringBatchDto BuildBatch(AgentOrchestratorKernel kernel, Goal goal, long sinceEventId)
    {
        return BuildBatch(kernel, goal, sinceEventId, operatorInbox: null, providerCapacity: null);
    }

    public static GoalMonitoringBatchDto BuildBatch(
        AgentOrchestratorKernel kernel,
        Goal goal,
        long sinceEventId,
        OperatorInboxReportDto? operatorInbox)
    {
        return BuildBatch(kernel, goal, sinceEventId, operatorInbox, providerCapacity: null);
    }

    public static GoalMonitoringBatchDto BuildBatch(
        AgentOrchestratorKernel kernel,
        Goal goal,
        long sinceEventId,
        OperatorInboxReportDto? operatorInbox,
        ProviderCapacityScheduleDto? providerCapacity,
        string? displayLabel = null)
    {
        var allEvents = BuildTimelineEvents(goal);
        var snapshot = BuildSnapshot(kernel, goal, allEvents.Count, operatorInbox, providerCapacity, displayLabel);
        return new GoalMonitoringBatchDto(
            goal.Id.Value,
            Math.Max(0, sinceEventId),
            allEvents.Count,
            snapshot,
            allEvents.Where(evt => evt.Id > sinceEventId).ToList(),
            StreamPath(goal.Id.Value));
    }

    public static async Task WriteServerSentEventAsync(
        Stream stream,
        string eventName,
        object payload,
        string? id,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(id))
        {
            builder.Append("id: ").Append(id).Append('\n');
        }

        builder.Append("event: ").Append(eventName).Append('\n');
        var json = JsonSerializer.Serialize(payload, DashboardJson.Options());
        foreach (var line in json.Split('\n'))
        {
            builder.Append("data: ").Append(line.TrimEnd('\r')).Append('\n');
        }

        builder.Append('\n');
        await stream.WriteAsync(Encoding.UTF8.GetBytes(builder.ToString()), cancellationToken);
    }

    public static async Task WriteKeepAliveAsync(Stream stream, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes($": {KeepAliveEventName} {DateTimeOffset.UtcNow:O}\n\n"), cancellationToken);
    }

    public static IReadOnlyList<ConductorTickEvent> BuildConductorTickEvents(IEnumerable<RunEventRecord> records)
    {
        return records
            .Where(record => record.EventType == RunEventTypes.ConductorTick)
            .Select(ToConductorTickEvent)
            .Where(evt => evt is not null)
            .Select(evt => evt!)
            .ToList();
    }

    public static IReadOnlyList<TaskStatusMonitoringEventDto> BuildTaskStatusEvents(GoalMonitoringBatchDto batch)
    {
        return batch.Events
            .Where(evt => evt.TaskId is not null && evt.TaskNumber is not null && evt.Role is not null && evt.TaskStatus is not null)
            .Select(evt => new TaskStatusMonitoringEventDto(
                batch.GoalId,
                evt.TaskId!,
                evt.TaskNumber!.Value,
                evt.Role!.Value,
                evt.TaskStatus!.Value,
                evt.Id,
                evt.OccurredAt))
            .ToList();
    }

    private static GoalMonitoringSnapshotDto BuildSnapshot(
        AgentOrchestratorKernel kernel,
        Goal goal,
        long lastEventId,
        OperatorInboxReportDto? operatorInbox,
        ProviderCapacityScheduleDto? providerCapacity,
        string? displayLabel)
    {
        return new GoalMonitoringSnapshotDto(
            goal.Id.Value,
            DateTimeOffset.UtcNow,
            lastEventId,
            DashboardResponseMapper.ToMonitorDto(kernel.BuildMonitor(goal.Id), displayLabel),
            goal.Tasks.Select(task => new TaskMonitoringSnapshotDto(
                ConsoleViews.GetTaskDisplayNumber(goal, task.Id),
                task.Id.Value,
                task.RequiredRole,
                task.Status,
                DashboardResponseMapper.ToProcessDto(task.LastProcess),
                task.SubscriptionRetryAfter)).ToList(),
            operatorInbox,
            providerCapacity);
    }

    private static List<GoalMonitoringEventDto> BuildTimelineEvents(Goal goal)
    {
        var taskById = goal.Tasks.ToDictionary(task => task.Id);
        return goal.Timeline
            .Select((evt, index) => new { Event = evt, Index = index })
            .OrderBy(item => item.Event.OccurredAt)
            .ThenBy(item => item.Index)
            .Select((item, index) => ToMonitoringEvent(goal, taskById, item.Event, index + 1))
            .ToList();
    }

    private static GoalMonitoringEventDto ToMonitoringEvent(
        Goal goal,
        Dictionary<TaskId, TaskSpec> taskById,
        ProgressEvent evt,
        long id)
    {
        TaskSpec? task = null;
        if (evt.TaskId is not null)
        {
            taskById.TryGetValue(evt.TaskId, out task);
        }

        var message = OutputTextPreview.CreateTimeline(evt.Message);
        return new GoalMonitoringEventDto(
            id,
            "timeline",
            goal.Id.Value,
            evt.TaskId?.Value,
            task is null ? null : ConsoleViews.GetTaskDisplayNumber(goal, task.Id),
            task?.RequiredRole,
            task?.Status,
            evt.Kind,
            message.Text,
            message.IsTruncated,
            message.OriginalLength,
            evt.OccurredAt);
    }

    public static string FormatEventId(long id) => id.ToString(CultureInfo.InvariantCulture);

    private static ConductorTickEvent? ToConductorTickEvent(RunEventRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.PayloadJson))
        {
            return null;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<ConductorTickPayload>(record.PayloadJson, CaseInsensitiveJson);
            return payload is null
                ? null
                : new ConductorTickEvent(
                    record.Sequence,
                    record.OccurredAt,
                    payload.Tick,
                    payload.Advanced,
                    payload.Held,
                    payload.Escalated,
                    payload.Retried,
                    payload.Done,
                    payload.WatchSleeping,
                    payload.ProgressLines);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record ConductorTickPayload(
        int Tick,
        int Advanced,
        int Held,
        int Escalated,
        int Retried,
        int Done,
        bool WatchSleeping,
        IReadOnlyList<string>? ProgressLines);
}
