using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class BackgroundDispatchRunner
{
    private bool TryAcceptDeferredNoChange(
        Goal goal,
        TaskSpec task,
        GoalWorktreeDispatchEvidence worktreeEvidence,
        string standardOutput,
        string standardError,
        ref string? standardErrorDiagnostic)
    {
        if (!DeveloperDeferredNoChangeQualifier.TryQualify(
                goal, task, worktreeEvidence.Head, worktreeEvidence.IsClean,
                worktreeEvidence.HasRelevantCommitAfterDispatch,
                standardOutput, standardError, _completionClassifier, out var outcome))
            return false;

        standardErrorDiagnostic = AppendDiagnostic(
            standardErrorDiagnostic ?? string.Empty, outcome.FormatMarker());
        return true;
    }
}
