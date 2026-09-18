namespace Mcg.AgentOrchestrator.Core;

public enum TaskBriefBudgetDisposition
{
    Full,
    Collapsed
}

public sealed record TaskBriefBudgetDecision(
    LogicalArtifactIdentity Identity,
    TaskBriefBudgetDisposition Disposition);

public sealed record TaskBriefSegment(
    IReadOnlyList<string> Lines,
    IReadOnlyList<string>? CollapsedLines = null,
    int CollapsePriority = int.MaxValue,
    LogicalArtifactIdentity? TypedProjectionIdentity = null,
    IReadOnlyList<AgentRole>? RoleVisibility = null)
{
    public static TaskBriefSegment Fixed(IReadOnlyList<string> lines) => new(lines);

    public static TaskBriefSegment Projected(
        string identity,
        IReadOnlyList<string> lines,
        IReadOnlyList<string>? collapsedLines = null,
        int collapsePriority = int.MaxValue) =>
        new(lines, collapsedLines, collapsePriority, new LogicalArtifactIdentity(identity));
}

public sealed record TaskBriefSource(
    GoalId GoalId,
    TaskId TaskId,
    AgentRole Role,
    string Title,
    IReadOnlyList<TaskBriefSegment> Segments,
    IReadOnlyList<TaskBriefBudgetDecision> BudgetDecisions,
    IReadOnlyList<string>? TrimmedPrerequisiteEvidenceRequestIds = null)
{
    public TaskBrief ToTaskBrief(string content) =>
        new(GoalId, TaskId, Role, Title, content, TrimmedPrerequisiteEvidenceRequestIds);

    public TaskBrief ProjectLegacyMarkedTextV1(bool emitTypedSourceBoundaries)
    {
        var lines = new List<string>();
        foreach (var segment in Segments)
        {
            if (emitTypedSourceBoundaries && segment.TypedProjectionIdentity is { } identity)
            {
                lines.Add(WorkerContextProjectionBoundary.Start(identity));
            }

            lines.AddRange(emitTypedSourceBoundaries
                ? segment.Lines.Select(WorkerContextProjectionBoundary.EscapeReservedLiteral)
                : segment.Lines);
            if (emitTypedSourceBoundaries && segment.TypedProjectionIdentity is { } closingIdentity)
            {
                lines.Add(WorkerContextProjectionBoundary.End(closingIdentity));
            }
        }

        return ToTaskBrief(string.Join(Environment.NewLine, lines));
    }
}

internal sealed record TaskBriefBudgetSelection(
    IReadOnlyList<TaskBriefSegment> Segments,
    IReadOnlyList<TaskBriefBudgetDecision> Decisions);
