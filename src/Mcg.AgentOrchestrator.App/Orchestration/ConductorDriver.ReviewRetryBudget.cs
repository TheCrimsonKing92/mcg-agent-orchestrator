using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private static string BuildReviewCapDecisionMessage(
        Goal goal,
        TaskSpec reviewerTask,
        ReviewRetryCapReceipt? receipt,
        string trigger,
        string outputArtifact,
        IReadOnlyList<ReviewFinding> ledger)
    {
        var open = ReviewFindings.GetOpenBlockingFindings(ledger, goal.EffectiveAcceptanceCriteriaCorrections);
        var stableIds = open.Count == 0 ? "unavailable" : string.Join(",", open.Select(finding => finding.StableId));
        var findings = open.Count == 0 ? TrimForConductorMessage(trigger) : string.Join("; ", open.Select(finding =>
            $"stable_id={finding.StableId} description={TrimForConductorMessage(finding.Description)}"));
        var candidateSha = reviewerTask.LastVerification?.ReviewedCommit ?? reviewerTask.LastDispatch?.BaseCommit ?? "missing";
        var prefix = receipt is { IsAtLifetimeBackstop: true }
            ? BuildLifetimeBackstopPrefix(receipt) + BuildOperatorDecisionSentence(goal, reviewerTask) + " "
            : $"auto-review-retry stopped {(receipt is null ? "because the system-owned review-cap receipt is missing" : $"at review round {receipt.Round}/{receipt.StopRound}")}: " +
                $"blocked-at-cap for Reviewer task {reviewerTask.Id.Value[..8]}; " + BuildOperatorDecisionSentence(goal, reviewerTask) + " ";
        return prefix + $"candidate_sha={candidateSha}; surviving_stable_ids={stableIds}; findings: {findings}. " +
            $"The goal remains non-terminal and cannot advance to acceptance. Full reviewer output: {outputArtifact}";
    }

    private static string BuildTesterReviewCapDecisionMessage(
        Goal goal, TaskSpec tester, TaskSpec target, ReviewRetryCapReceipt receipt, string finding, string outputArtifact) =>
        (receipt is { IsAtLifetimeBackstop: true }
            ? BuildLifetimeBackstopPrefix(receipt)
            : $"auto-review-retry stopped at review round {receipt.Round}/{receipt.StopRound} for task {target.Id.Value[..8]}; ") +
        BuildOperatorDecisionSentence(goal, tester) + $" Findings: {TrimForConductorMessage(finding)}. " +
        $"Full {tester.RequiredRole.ToString().ToLowerInvariant()} output: {outputArtifact}";

    private static string BuildLifetimeBackstopPrefix(ReviewRetryCapReceipt receipt) =>
        $"auto-review-retry stopped at lifetime backstop: total review round {receipt.LifetimeRound}/{receipt.LifetimeBackstop}; " +
        $"consecutive non-shrinking round {receipt.Round}/{receipt.StopRound}; ";

    private static string BuildOperatorDecisionSentence(Goal goal, TaskSpec fallback)
    {
        var reviewer = goal.Tasks.LastOrDefault(task => task.RequiredRole == AgentRole.Reviewer) ?? fallback;
        var number = TaskDisplayNumber.Resolve(goal, reviewer.Id);
        var prefix = goal.Id.Value[..8];
        return $"operator decision required: grant a fresh review budget by retrying Reviewer task {number} with retry --goal {prefix} {number} --text-file <path> --cause <cause>, " +
            $"waive the applicable criterion as an explicit override with goal-amend {prefix} --waive <exact-text> --reason <reason>, " +
            $"or supersede the requirement with supersede-goal {prefix} <reason>.";
    }

    private static string InsertReviewBudgetResetMarker(string message, string? marker)
    {
        if (marker is null) return message;
        var newline = message.IndexOf('\n');
        return newline < 0 ? message + Environment.NewLine + marker : message.Insert(newline + 1, marker + Environment.NewLine);
    }
}
