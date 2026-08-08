namespace Mcg.AgentOrchestrator.Core;

public sealed record GoalBriefVersion(
    int Version,
    string Text,
    DateTimeOffset RecordedAt,
    string? Reason = null,
    int? SupersededByVersion = null)
{
    public bool IsAuthoritative => SupersededByVersion is null;

    public bool IsSuperseded => SupersededByVersion is not null;
}

public sealed record GoalBriefRevisionResult(
    GoalId GoalId,
    GoalBriefVersion AuthoritativeVersion,
    IReadOnlyList<TaskId> NotYetStartedTaskIds,
    IReadOnlyList<TaskId> InFlightTaskIds,
    IReadOnlyList<TaskId> CompletedTaskIds);

public sealed record GoalBriefAnswerSupersession(
    HumanInputRequestId RequestId,
    string ReplacementAnswer);

public sealed class GoalBriefRevisionNotAllowedException(string message) : InvalidOperationException(message);

public sealed class GoalBriefRevisionNoChangeException(string message) : InvalidOperationException(message);
