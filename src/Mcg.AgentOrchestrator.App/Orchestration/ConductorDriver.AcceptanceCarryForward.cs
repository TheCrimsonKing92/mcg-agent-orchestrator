using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private const string AcceptanceMainAdvanceOperation = "conductor:acceptance-main-advance";
    private const string AcceptanceMainAdvanceCarryOperation = "conductor:acceptance-main-advance-carry";

    private ConductorAdvanceResult CompleteLandingAfterRacingLandingCarryForward(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        AcceptanceVerificationSummary acceptance)
    {
        var disposition = CarryForwardGreenVerdict(candidate, acceptance);
        if (disposition is not (
            AcceptanceMainAdvanceDisposition.Disjoint or
            AcceptanceMainAdvanceDisposition.Unchanged))
        {
            return MakeResult(
                candidate.Goal.Id.Value,
                candidate.GoalPrefix,
                policy,
                new ConductorAdvanceOutcome.Held(
                    GoalLifecycleState.Verified,
                    $"Main advanced with overlapping or unclassifiable changes; " +
                    $"revalidation required before landing. {disposition.FormatReceipt()}"));
        }

        return CompleteLandingAfterAcceptance(candidate.Goal, candidate.GoalPrefix, policy, acceptance);
    }

    internal AcceptanceMainAdvanceDisposition CarryForwardGreenVerdict(
        ConductorParallelAcceptanceCandidate candidate,
        AcceptanceVerificationSummary acceptance)
    {
        if (!acceptance.Passed || acceptance.RequiredUnmetCriteria.Count > 0)
        {
            return new AcceptanceMainAdvanceDisposition.Unknown("verdict-not-green");
        }

        AcceptanceMainAdvanceDisposition disposition;
        string? currentMainSha = null;
        try
        {
            disposition = ClassifyMainAdvance(candidate, acceptance, out var currentBranchSha, out currentMainSha);
            if (disposition is AcceptanceMainAdvanceDisposition.Disjoint)
            {
                GoalOperationJournal.AcceptancePassed(
                    _executionDirectory!,
                    candidate.Goal,
                    AcceptanceMainAdvanceCarryOperation,
                    currentBranchSha,
                    currentMainSha,
                    BuildCarryForwardReceipt(disposition, acceptance, currentBranchSha!, currentMainSha!));
            }
            else if (disposition is not AcceptanceMainAdvanceDisposition.Unchanged)
            {
                RecordMainAdvanceDiagnostic(candidate.Goal, disposition, currentMainSha);
            }
        }
        catch (Exception ex)
        {
            disposition = new AcceptanceMainAdvanceDisposition.Unknown(
                $"exception-{ex.GetType().Name}:{Bound(ex.Message, 240)}");
            RecordMainAdvanceDiagnostic(candidate.Goal, disposition, currentMainSha);
        }

        return disposition;
    }

    private AcceptanceMainAdvanceDisposition ClassifyMainAdvance(
        ConductorParallelAcceptanceCandidate candidate,
        AcceptanceVerificationSummary acceptance,
        out string? currentBranchSha,
        out string? currentMainSha)
    {
        currentBranchSha = null;
        currentMainSha = null;
        if (_executionDirectory is null)
        {
            return new AcceptanceMainAdvanceDisposition.Unknown("execution-directory-unavailable");
        }

        if (string.IsNullOrWhiteSpace(candidate.BranchHeadSha) ||
            string.IsNullOrWhiteSpace(candidate.MainHeadSha) ||
            string.IsNullOrWhiteSpace(acceptance.BranchHeadSha) ||
            string.IsNullOrWhiteSpace(acceptance.MainHeadSha))
        {
            return new AcceptanceMainAdvanceDisposition.Unknown("verified-candidate-sha-unavailable");
        }

        if (!candidate.BranchHeadSha.Equals(acceptance.BranchHeadSha, StringComparison.OrdinalIgnoreCase) ||
            !candidate.MainHeadSha.Equals(acceptance.MainHeadSha, StringComparison.OrdinalIgnoreCase))
        {
            return new AcceptanceMainAdvanceDisposition.Unknown("candidate-and-verdict-sha-mismatch");
        }

        var worktreePath = GoalWorktrees.TryResolve(_executionDirectory, candidate.Goal.Id);
        if (worktreePath is null)
        {
            return new AcceptanceMainAdvanceDisposition.Unknown("goal-worktree-unavailable");
        }

        currentBranchSha = TryResolveGitHead(worktreePath);
        currentMainSha = TryResolveGitHead(_executionDirectory);
        if (string.IsNullOrWhiteSpace(currentBranchSha) || string.IsNullOrWhiteSpace(currentMainSha))
        {
            return new AcceptanceMainAdvanceDisposition.Unknown("current-candidate-sha-unavailable");
        }

        if (candidate.MainHeadSha.Equals(currentMainSha, StringComparison.OrdinalIgnoreCase))
        {
            return new AcceptanceMainAdvanceDisposition.Unchanged();
        }

        var ancestry = GitCli.Run(
            _executionDirectory,
            "merge-base",
            "--is-ancestor",
            candidate.MainHeadSha,
            currentMainSha);
        if (!GitSucceeded(ancestry))
        {
            return new AcceptanceMainAdvanceDisposition.Unknown("verified-main-not-ancestor-of-current-main");
        }

        var landed = ReadChangedPaths(_executionDirectory, candidate.MainHeadSha, currentMainSha);
        var verified = ReadChangedPaths(_executionDirectory, candidate.MainHeadSha, candidate.BranchHeadSha);
        var postRebase = ReadChangedPaths(_executionDirectory, currentMainSha, currentBranchSha);
        if (landed is null || verified is null || postRebase is null)
        {
            return new AcceptanceMainAdvanceDisposition.Unknown("git-changed-paths-unavailable");
        }

        return AcceptanceMainAdvanceClassifier.Classify(
            candidate.MainHeadSha,
            currentMainSha,
            candidate.ScopePaths,
            verified,
            postRebase,
            landed);
    }

    private static IReadOnlyList<string>? ReadChangedPaths(
        string workingDirectory,
        string oldRevision,
        string newRevision)
    {
        var result = GitCli.Run(
            workingDirectory,
            "diff",
            "--name-only",
            "-z",
            "--no-renames",
            oldRevision,
            newRevision);
        return GitSucceeded(result)
            ? result.Output
                .Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(RepositoryPathOverlap.Normalize)
                .Where(path => path.Length > 0)
                .ToArray()
            : null;
    }

    private static bool GitSucceeded(GitCli.GitResult result) =>
        result.Succeeded && !result.DrainTimedOut;

    private static string BuildCarryForwardReceipt(
        AcceptanceMainAdvanceDisposition disposition,
        AcceptanceVerificationSummary acceptance,
        string currentBranchSha,
        string currentMainSha) =>
        $"{disposition.FormatReceipt()} " +
        $"verifiedBranch={Bound(acceptance.BranchHeadSha, 40)} " +
        $"verifiedMain={Bound(acceptance.MainHeadSha, 40)} " +
        $"currentBranch={Bound(currentBranchSha, 40)} currentMain={Bound(currentMainSha, 40)}";

    private void RecordMainAdvanceDiagnostic(
        Goal goal,
        AcceptanceMainAdvanceDisposition disposition,
        string? currentMainSha)
    {
        if (_executionDirectory is null)
        {
            return;
        }

        try
        {
            GoalOperationJournal.Completed(
                _executionDirectory,
                goal,
                AcceptanceMainAdvanceOperation,
                disposition.FormatReceipt(),
                currentMainSha);
        }
        catch
        {
            // Diagnostics must not replace today's safe re-gate behavior when evidence storage is unavailable.
        }
    }

    private static string Bound(string? value, int maxLength)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }
}
