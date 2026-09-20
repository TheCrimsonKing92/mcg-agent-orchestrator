namespace Mcg.AgentOrchestrator.Core;

public sealed record TaskBrief(
    GoalId GoalId,
    TaskId TaskId,
    AgentRole Role,
    string Title,
    string Content,
    // Request ids whose answered prerequisite evidence did not fit the bounded section. The brief
    // already names them in an in-prompt budget note; the dispatcher turns this into a TaskNote so
    // the trim is visible on the timeline as well. BuildTaskBrief is a query used by the CLI and
    // dashboard previews, so it must not write the note itself.
    IReadOnlyList<string>? TrimmedPrerequisiteEvidenceRequestIds = null);
