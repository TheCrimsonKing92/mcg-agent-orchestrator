using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Detached kernels do not emit events: mirror only the delta that their durable merge accepted.
internal static class GoalEventsTimelineMirror
{
    private readonly record struct EventKey(string Kind, long Ticks, string Message, string? TaskId);

    internal static IReadOnlyList<ProgressEventSnapshot> AddedEntries(
        IEnumerable<ProgressEvent> before, IReadOnlyList<ProgressEventSnapshot> after)
    {
        var remaining = before.GroupBy(entry => Key(entry.Kind, entry.OccurredAt, entry.Message, entry.TaskId?.Value))
            .ToDictionary(group => group.Key, group => group.Count());
        return after.Where(entry => !Consume(remaining, Key(entry.Kind, entry.OccurredAt, entry.Message, entry.TaskId)))
            .ToArray();
    }

    internal static void AppendMissing(
        GoalLifecycleEventWriter writer, GoalId goalId,
        IReadOnlyList<ProgressEventSnapshot> candidates, string source)
    {
        if (candidates.Count == 0)
            return;

        try
        {
            var remaining = new Dictionary<EventKey, int>();
            var path = writer.EventFilePath(goalId);
            if (File.Exists(path))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line)
                {
                    try
                    {
                        if (StateLogDivergenceComparer.ParseLine(line) is not { } parsed)
                            continue;
                        var entry = parsed.Entry;
                        var key = new EventKey(entry.Kind, entry.OccurredAtUtcTicks, entry.Message, entry.TaskId);
                        remaining[key] = remaining.GetValueOrDefault(key) + 1;
                    }
                    catch (Exception ex) when (ex is JsonException or FormatException)
                    {
                        // A legacy or torn line cannot prove that this timeline event was delivered.
                    }
                }
            }

            foreach (var entry in candidates)
            {
                if (Consume(remaining, Key(entry.Kind, entry.OccurredAt, entry.Message, entry.TaskId)))
                    continue;
                writer.AppendTimelineEvent(new ProgressEvent(
                    new GoalId(entry.GoalId), entry.TaskId is null ? null : new TaskId(entry.TaskId),
                    entry.Kind, entry.Message, entry.OccurredAt, entry.RequeueSkipped, entry.OperatorGates,
                    entry.OperatorIntentApplied, entry.HumanInputSuperseded, entry.TickOutcome));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"GOAL_EVENTS_MIRROR_WARNING goal={goalId.Value[..Math.Min(8, goalId.Value.Length)]} source={source} exception={ex.GetType().Name}");
        }
    }

    private static EventKey Key(ProgressKind kind, DateTimeOffset occurredAt, string message, string? taskId) =>
        new(kind.ToString(), occurredAt.UtcTicks, message, taskId);

    private static bool Consume(Dictionary<EventKey, int> remaining, EventKey key)
    {
        if (!remaining.TryGetValue(key, out var count) || count == 0)
            return false;
        remaining[key] = count - 1;
        return true;
    }
}
