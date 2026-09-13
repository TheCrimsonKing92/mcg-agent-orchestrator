namespace Mcg.AgentOrchestrator.Core;

/// <summary>
/// Records the timeline half of the prerequisite-evidence budget-overflow decision: whenever the
/// bounded section dropped an answered request, the same request ids that the in-prompt budget note
/// names are written to the task timeline, so a trimmed answer is never silently lost.
/// </summary>
/// <remarks>
/// This lives beside <see cref="PrerequisiteEvidenceDigest"/> rather than inside a dispatcher
/// because every path that turns a brief into an emitted prompt owes the same note, and each one
/// had to grow its own copy otherwise. <see cref="AgentOrchestratorKernel.BuildTaskBrief"/> cannot
/// record it itself: it is a query that CLI and dashboard previews re-run on every refresh, so
/// recording there would write a timeline entry for a prompt that was never emitted.
/// </remarks>
public static class PrerequisiteEvidenceTrimNote
{
    /// <summary>Note kind written to the task timeline; consumers match on this prefix.</summary>
    public const string NoteKind = "prerequisite-evidence-trimmed";

    /// <summary>
    /// Writes the note when <paramref name="brief"/> trimmed at least one answered request.
    /// Returns whether a note was written, so a caller can assert the no-trim case wrote nothing.
    /// </summary>
    public static bool RecordIfTrimmed(
        AgentOrchestratorKernel kernel,
        GoalId goalId,
        TaskId taskId,
        TaskBrief brief)
    {
        if (brief.TrimmedPrerequisiteEvidenceRequestIds is not { Count: > 0 } trimmedRequestIds)
        {
            return false;
        }

        kernel.RecordTaskNote(
            goalId,
            taskId,
            $"kind={NoteKind} request_ids={string.Join(",", trimmedRequestIds)}");
        return true;
    }
}
