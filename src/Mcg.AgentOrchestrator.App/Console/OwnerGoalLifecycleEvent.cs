using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Only structural fields cross into presentation; free-form messages may contain JSON or commands.
internal static class OwnerGoalLifecycleEvent
{
    internal static bool TryParse(string line, string goalId, out OwnerConductEvent? item)
    {
        item = null;
        try
        {
            using var document = JsonDocument.Parse(line.TrimStart('\uFEFF'));
            var root = document.RootElement;
            var timestamp = root.TryGetProperty("timestamp", out var time) ? time : root.GetProperty("occurredAt");
            var kind = root.GetProperty("eventType").GetString();
            if (string.IsNullOrEmpty(kind) || !kind.All(char.IsLetter)) return false;
            var detail = kind;
            if (root.TryGetProperty("taskId", out var task) && task.GetString() is { } taskId)
                detail += " task=" + taskId;
            if (root.TryGetProperty("role", out var role) && Enum.TryParse<AgentRole>(role.GetString(), out var parsedRole))
                detail += " role=" + parsedRole;
            // The writer has no structured outcome field. Recognize only the kernel's
            // explicit blocking-finding marker; never copy arbitrary message payloads.
            if (kind == "TaskFailed" && root.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String &&
                (message.GetString()!.StartsWith("Tester WORKER_RESULT rejected: merged structured finding state still has open blocking stable_id(s):", StringComparison.Ordinal) ||
                 message.GetString()!.StartsWith("Reviewer WORKER_RESULT verdict rejected: merged structured finding state still has open blocking stable_id(s):", StringComparison.Ordinal)))
                detail += " outcome=finding";
            item = new(timestamp.GetDateTimeOffset(), "goal-lifecycle", goalId, detail);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { return false; }
    }

    internal static OwnerConductEvent WithRole(OwnerConductEvent item, IReadOnlyDictionary<string, AgentRole> roles)
    {
        if (item.EventKind != "goal-lifecycle") return item;
        var tokens = item.Detail.Split(' ');
        if (tokens.Any(token => token.StartsWith("role=", StringComparison.Ordinal))) return item;
        var taskId = tokens.FirstOrDefault(token => token.StartsWith("task=", StringComparison.Ordinal))?[5..];
        return taskId is not null && roles.TryGetValue(taskId, out var role)
            ? item with { Detail = item.Detail + " role=" + role } : item;
    }
}
