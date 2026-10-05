using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record BoardFillDraftAssessment(BoardFillPremiseVerification Verification,
    IReadOnlyList<AuthorBriefDraftCheck> PreflightChecks, BoardFillScopeProposal Scope,
    bool Fileable, IReadOnlyList<string> FailingReasons);

internal static class BoardFillFileability
{
    internal static BoardFillDraftAssessment Evaluate(BoardFillDraftRound round, string? markdown,
        BoardFillPremiseVerification verification, IReadOnlyList<AuthorBriefDraftCheck> preflight,
        BoardFillScopeProposal scope, BacklogItem? current, IReadOnlyList<Goal> goals,
        Func<BacklogItem, BacklogReadiness> readiness)
    {
        var reasons = new List<string>();
        if (round.Outcome != "draft") reasons.Add("outcome:" + round.Outcome);
        if (round.Checks.Count == 0) reasons.Add("structural:missing-checks");
        reasons.AddRange(round.Checks.Where(check => !check.Passed).Select(check => "structural:" + check.Name));
        if (markdown is null) reasons.Add("draft-unreadable");
        if (verification.Status != "complete")
            reasons.Add($"verification:{verification.Status}" + (verification.Detail is null ? "" : " " + verification.Detail));
        // Do not accept vacuous truth or incomplete injected results as verification.
        if (verification.Status == "complete" && (verification.BulletCount == 0 ||
            verification.BulletCount != BoardFillVerifierContract.BulletCount(BoardFillVerifierContract.Premise(markdown ?? "")) ||
            verification.Verdicts.Count != verification.BulletCount ||
            !verification.Verdicts.Select(verdict => verdict.Bullet).Order().SequenceEqual(Enumerable.Range(1, verification.BulletCount))))
            reasons.Add("verification:incomplete");
        reasons.AddRange(verification.Verdicts.Where(verdict => verdict.Verdict != "verified")
            .Select(verdict => $"premise-bullet-{verdict.Bullet}:{verdict.Verdict}"));
        if (preflight.Count != 4) reasons.Add("preflight:missing-checks");
        reasons.AddRange(preflight.Where(check => !check.Passed).Select(check => check.Name));
        if (current is null) reasons.Add("backlog-missing");
        else
        {
            if (BoardFillReadyItemSelector.ChangeStamp(current) != round.ChangeStamp) reasons.Add("backlog-changed");
            reasons.AddRange(goals.Where(goal => goal.SourceBacklogItemId == current.Id && !goal.IsTerminal)
                .OrderBy(goal => goal.Id.Value, StringComparer.Ordinal).Select(goal => "backlog-linked-goal:" + goal.Id.Value));
            if (BoardFillReadyItemSelector.Select([current], goals, new HashSet<string>(), readiness) is null)
                reasons.Add("backlog-ineligible");
        }
        return new(verification, preflight, scope, reasons.Count == 0, reasons);
    }
}
