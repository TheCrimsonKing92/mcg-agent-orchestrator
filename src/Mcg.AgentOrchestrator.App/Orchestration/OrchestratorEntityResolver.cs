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
        .Where(request =>
            !request.IsCompleted &&
            request.Id.Value.StartsWith(idOrPrefix, StringComparison.OrdinalIgnoreCase))
        .ToList();

    if (matches.Count == 1)
    {
        return matches[0];
    }

    if (matches.Count > 1)
    {
        throw BuildAmbiguousHumanInputException(idOrPrefix, matches);
    }

    var completedMatches = kernel.HumanInputRequests
        .Where(request =>
            request.IsCompleted &&
            request.Id.Value.StartsWith(idOrPrefix, StringComparison.OrdinalIgnoreCase))
        .ToList();
    if (completedMatches.Count == 1)
    {
        return completedMatches[0];
    }

    if (completedMatches.Count > 1)
    {
        throw BuildAmbiguousHumanInputException(idOrPrefix, completedMatches);
    }

    var goals = kernel.Goals
        .Where(goal => goal.Id.Value.StartsWith(idOrPrefix, StringComparison.OrdinalIgnoreCase))
        .ToList();
    if (goals.Count == 1)
    {
        var openForGoal = kernel.GetPendingHumanInput(goals[0].Id);
        return openForGoal.Count switch
        {
            1 => openForGoal[0],
            0 => throw new KeyNotFoundException(
                $"Goal '{goals[0].Id.Value}' has no open human input requests."),
            _ => throw BuildAmbiguousHumanInputException(idOrPrefix, openForGoal)
        };
    }

    if (goals.Count > 1)
    {
        throw new InvalidOperationException(
            $"Goal prefix '{idOrPrefix}' is ambiguous. Candidates: {string.Join(", ", goals.Select(goal => goal.Id.Value))}");
    }

    throw new KeyNotFoundException($"Human input request or goal '{idOrPrefix}' was not found.");
}

public static HumanInputRequest ResolveHumanInputRequest(
    AgentOrchestratorKernel kernel,
    GoalId goalId,
    string idOrPrefix)
{
    var matches = kernel.HumanInputRequests
        .Where(request =>
            request.GoalId == goalId &&
            request.Id.Value.StartsWith(idOrPrefix, StringComparison.OrdinalIgnoreCase))
        .ToArray();
    return matches.Length switch
    {
        1 => matches[0],
        0 => throw new KeyNotFoundException(
            $"Human input request '{idOrPrefix}' was not found on goal '{goalId.Value}'."),
        _ => throw BuildAmbiguousHumanInputException(idOrPrefix, matches)
    };
}

public static (Goal Goal, string SourceRecordId) ResolveOperatorTaskNoteGateSource(
    AgentOrchestratorKernel kernel,
    string idOrPrefix)
{
    var matches = kernel.Goals
        .SelectMany(goal => goal.Timeline
            .Where(evt => evt.Kind == ProgressKind.OperatorTaskNote)
            .SelectMany(evt => evt.OperatorGates ?? [])
            .Where(gate => gate.SourceRecordId.StartsWith(idOrPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(gate => (Goal: goal, gate.SourceRecordId)))
        .Distinct()
        .ToArray();
    return matches.Length switch
    {
        1 => matches[0],
        0 => throw new KeyNotFoundException($"Operator task-note gate source '{idOrPrefix}' was not found."),
        _ => throw new InvalidOperationException(
            $"Operator task-note gate source '{idOrPrefix}' is ambiguous. Candidates: " +
            string.Join(", ", matches.Select(match => match.SourceRecordId)))
    };
}

private static InvalidOperationException BuildAmbiguousHumanInputException(
    string idOrPrefix,
    IReadOnlyList<HumanInputRequest> requests) =>
    new($"Human input selector '{idOrPrefix}' is ambiguous. Candidates: " +
        string.Join("; ", requests.Select(request =>
            $"{request.Id.Value} (goal {request.GoalId.Value}): {request.Question}")));

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


