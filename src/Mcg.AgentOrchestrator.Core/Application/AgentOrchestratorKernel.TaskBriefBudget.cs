namespace Mcg.AgentOrchestrator.Core;

public sealed partial class AgentOrchestratorKernel
{
    private static TaskBriefBudgetSelection ApplyTaskBriefBudget(
        IReadOnlyList<TaskBriefSegment> segments,
        AgentRole role,
        bool usesFileAccessContext,
        bool measureWithTypedSourceBoundaries)
    {
        var budget = TaskBriefCharacterBudget(role, usesFileAccessContext);
        if (!usesFileAccessContext || CountTaskBriefCharacters(segments, measureWithTypedSourceBoundaries) <= budget)
        {
            return CreateTaskBriefBudgetSelection(segments, new HashSet<LogicalArtifactIdentity>());
        }

        var collapsedSegments = segments.ToList();
        var collapsedIdentities = new HashSet<LogicalArtifactIdentity>();
        foreach (var index in collapsedSegments
            .Select((segment, index) => new { segment, index })
            .Where(item => item.segment.CollapsedLines is not null)
            .OrderBy(item => item.segment.CollapsePriority)
            .Select(item => item.index))
        {
            var segment = collapsedSegments[index];
            collapsedSegments[index] = segment with
            {
                Lines = segment.CollapsedLines!,
                CollapsedLines = null
            };
            if (segment.TypedProjectionIdentity is { } identity)
            {
                collapsedIdentities.Add(identity);
            }

            if (CountTaskBriefCharacters(collapsedSegments, measureWithTypedSourceBoundaries) <= budget)
            {
                break;
            }
        }

        return CreateTaskBriefBudgetSelection(collapsedSegments, collapsedIdentities);
    }

    private static TaskBriefBudgetSelection CreateTaskBriefBudgetSelection(
        IReadOnlyList<TaskBriefSegment> segments,
        IReadOnlySet<LogicalArtifactIdentity> collapsedIdentities) =>
        new(
            segments,
            segments
                .Where(segment => segment.TypedProjectionIdentity is not null)
                .Select(segment => new TaskBriefBudgetDecision(
                    segment.TypedProjectionIdentity.GetValueOrDefault(),
                    collapsedIdentities.Contains(segment.TypedProjectionIdentity.GetValueOrDefault())
                        ? TaskBriefBudgetDisposition.Collapsed
                        : TaskBriefBudgetDisposition.Full))
                .ToArray());

    private static int CountTaskBriefCharacters(
        IReadOnlyList<TaskBriefSegment> segments,
        bool includeTypedSourceBoundaries)
    {
        var characterCount = 0;
        var lineCount = 0;
        foreach (var segment in segments)
        {
            if (includeTypedSourceBoundaries && segment.TypedProjectionIdentity is { } identity)
            {
                characterCount += WorkerContextProjectionBoundary.Start(identity).Length;
                lineCount++;
            }

            foreach (var line in segment.Lines)
            {
                characterCount += includeTypedSourceBoundaries
                    ? WorkerContextProjectionBoundary.EscapeReservedLiteral(line).Length
                    : line.Length;
                lineCount++;
            }

            if (includeTypedSourceBoundaries && segment.TypedProjectionIdentity is { } closingIdentity)
            {
                characterCount += WorkerContextProjectionBoundary.End(closingIdentity).Length;
                lineCount++;
            }
        }

        return characterCount + Math.Max(0, lineCount - 1) * Environment.NewLine.Length;
    }
}
