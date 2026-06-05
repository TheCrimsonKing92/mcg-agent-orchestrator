using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class OrchestratorEntityResolver
{
public static Goal? GetLatestGoal(AgentOrchestratorKernel kernel)
{
    return kernel.Goals
        .OrderByDescending(goal => goal.Timeline.FirstOrDefault()?.OccurredAt ?? DateTimeOffset.MinValue)
        .FirstOrDefault();
}

public static Goal ResolveGoal(AgentOrchestratorKernel kernel, Goal? currentGoal, string? idOrPrefix)
{
    if (string.IsNullOrWhiteSpace(idOrPrefix))
    {
        return RequireGoal(currentGoal ?? GetLatestGoal(kernel));
    }

    var matches = kernel.Goals.Where(goal => goal.Id.Value.StartsWith(idOrPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
    return matches.Count switch
    {
        1 => matches[0],
        0 => throw new KeyNotFoundException($"Goal '{idOrPrefix}' was not found."),
        _ => throw new InvalidOperationException($"Goal prefix '{idOrPrefix}' is ambiguous.")
    };
}

public static HumanInputRequest ResolveHumanInputRequest(AgentOrchestratorKernel kernel, string idOrPrefix)
{
    var matches = kernel.HumanInputRequests
        .Where(request => request.Id.Value.StartsWith(idOrPrefix, StringComparison.OrdinalIgnoreCase))
        .ToList();

    return matches.Count switch
    {
        1 => matches[0],
        0 => throw new KeyNotFoundException($"Human input request '{idOrPrefix}' was not found."),
        _ => throw new InvalidOperationException($"Human input request prefix '{idOrPrefix}' is ambiguous.")
    };
}

public static Goal RequireGoal(Goal? goal)
{
    return goal ?? throw new InvalidOperationException("Create a goal first with: goal <objective>");
}

public static TaskSpec GetTaskByDisplayNumber(Goal goal, string value)
{
    if (int.TryParse(value, out var displayNumber))
    {
        if (displayNumber < 1 || displayNumber > goal.Tasks.Count)
        {
            throw new KeyNotFoundException($"Task number '{displayNumber}' was not found; goal has {goal.Tasks.Count} task(s).");
        }

        return goal.Tasks[displayNumber - 1];
    }

    var matches = goal.Tasks
        .Where(task => task.Id.Value.StartsWith(value, StringComparison.OrdinalIgnoreCase))
        .ToList();

    return matches.Count switch
    {
        1 => matches[0],
        0 => throw new KeyNotFoundException($"Task '{value}' was not found."),
        _ => throw new InvalidOperationException($"Task id prefix '{value}' is ambiguous.")
    };
}
}


