using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class WorktreeEditPreservation
{
    internal static string Preserve(string workingDirectory, string stashMessage)
    {
        try
        {
            var status = GitCli.Run(workingDirectory, "status", "--porcelain", "--untracked-files=all");
            if (!status.Succeeded)
                return $"failed=status:{Bound(status.Error)}";
            if (string.IsNullOrWhiteSpace(status.Output))
                return "clean-no-edits";

            var stash = GitCli.Run(workingDirectory, "stash", "push", "--include-untracked", "-m", stashMessage);
            if (!stash.Succeeded)
                return $"failed=stash:{Bound(stash.Error)}";

            var clean = GitCli.Run(workingDirectory, "status", "--porcelain", "--untracked-files=all");
            if (!clean.Succeeded || !string.IsNullOrWhiteSpace(clean.Output))
                return $"failed=worktree-not-clean:{Bound(clean.Error + clean.Output)}";

            var reference = GitCli.Run(workingDirectory, "stash", "list", "-1", "--format=%H");
            var stashRef = reference.Succeeded && !string.IsNullOrWhiteSpace(reference.Output)
                ? reference.Output.Trim()
                : "stash-created-ref-unavailable";
            return $"preserved={stashRef}";
        }
        catch (Exception ex)
        {
            return $"failed=exception:{Bound(ex.Message)}";
        }
    }

    private static string Bound(string? value) =>
        ProgressiveReviewGlanceCoordinator.BoundSingleLineForSteering(value, 240).Replace(' ', '-');
}
