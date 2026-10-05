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
                standardOutput, standardError, _completionClassifier, out var outcome, out var declineCode))
        {
            standardErrorDiagnostic = AppendDeferredNoChangeDecline(task, standardOutput,
                standardErrorDiagnostic, declineCode);
            return false;
        }

        standardErrorDiagnostic = AppendDiagnostic(
            standardErrorDiagnostic ?? string.Empty, outcome.FormatMarker());
        return true;
    }

    private string WithDeferredNoChangeDecline(
        Goal goal, TaskSpec task, GoalWorktreeDispatchEvidence worktreeEvidence,
        string? standardOutput, string standardError, string? diagnostic)
    {
        if (standardOutput is null) return diagnostic ?? string.Empty;
        DeveloperDeferredNoChangeQualifier.TryQualify(
            goal, task, worktreeEvidence.Head, worktreeEvidence.IsClean,
            worktreeEvidence.HasRelevantCommitAfterDispatch,
            standardOutput, standardError, _completionClassifier, out _, out var declineCode);
        return AppendDeferredNoChangeDecline(task, standardOutput, diagnostic, declineCode);
    }

    private string AppendDeferredNoChangeDecline(
        TaskSpec task, string standardOutput, string? diagnostic, string? declineCode)
    {
        if (task.RequiredRole != AgentRole.Developer || declineCode is null ||
            !_completionClassifier.HasExplicitNoChangeRationale(standardOutput, string.Empty))
            return diagnostic ?? string.Empty;
        return AppendDiagnostic(diagnostic ?? string.Empty, "DEFERRED_NO_CHANGE_DECLINED reason=" + declineCode);
    }
}
