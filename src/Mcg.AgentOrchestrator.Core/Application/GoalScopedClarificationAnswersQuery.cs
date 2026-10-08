namespace Mcg.AgentOrchestrator.Core;

internal sealed record GoalScopedClarificationAnswer(
    string RequestId,
    AgentRole? AskingRole,
    string Question,
    string AnswerText,
    HumanInputAnswerOrigin Origin,
    int? AnsweredBriefVersion,
    DateTimeOffset AnsweredAt);

/// <summary>Reads sibling-task rulings without changing task-private resolved or pending input.</summary>
internal static class GoalScopedClarificationAnswersQuery
{
    internal static IReadOnlyList<GoalScopedClarificationAnswer> Select(
        IEnumerable<HumanInputRequest> requests,
        Goal goal,
        TaskId taskId) =>
        requests
            .Where(request =>
                request.GoalId == goal.Id &&
                request.TaskId is not null &&
                request.TaskId != taskId &&
                request.Kind != HumanWaitKind.PlannerPrerequisiteEvidence &&
                request.IsCompleted &&
                !request.WasDismissed &&
                !request.IsSyntheticParkedHumanWaitCompletion &&
                request.SupersededByRequestId is null &&
                !string.IsNullOrWhiteSpace(request.Answer))
            .Select(request => new GoalScopedClarificationAnswer(
                request.Id.Value,
                goal.Tasks.FirstOrDefault(task => task.Id == request.TaskId)?.RequiredRole,
                request.Question,
                request.AuthoritativeAnswer!.Text,
                request.AuthoritativeAnswer.Origin,
                request.AuthoritativeAnswer.BriefVersion,
                request.AuthoritativeAnswer.AnsweredAt))
            .ToList();
}
