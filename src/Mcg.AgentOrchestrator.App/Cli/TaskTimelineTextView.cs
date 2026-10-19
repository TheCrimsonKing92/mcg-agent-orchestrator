using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class TaskTimelineTextView
{
public static void PrintTimeline(Goal goal)
{
    Console.WriteLine("Timeline:");
    foreach (var item in goal.Timeline.OrderBy(evt => evt.OccurredAt))
    {
        var task = item.TaskId is null ? "goal" : item.TaskId.Value[..8];
        Console.WriteLine($"  {item.OccurredAt:u} {item.Kind} {task}: {OutputTextPreview.CreateTimeline(item.Message).Text}");
    }
}

public static void PrintTaskTimeline(Goal goal, TaskSpec task, int? limit = null)
{
    var events = goal.Timeline.Where(evt => evt.TaskId == task.Id).OrderBy(evt => evt.OccurredAt).ToList();
    if (limit is { } count && events.Count > count)
    {
        events = events.TakeLast(count).ToList();
    }
    if (events.Count == 0)
    {
        Console.WriteLine("  no task timeline events");
        return;
    }

    foreach (var item in events)
    {
        Console.WriteLine($"  {item.OccurredAt:u} {item.Kind}: {OneLine(OutputTextPreview.CreateTimeline(item.Message).Text)}");
    }
}

internal static string OneLine(string? value, string fallback = "unknown")
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return fallback;
    }

    const int maxLength = 400;
    var singleLine = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    return singleLine.Length <= maxLength ? singleLine : singleLine[..(maxLength - 14)] + "... [truncated]";
}

}
