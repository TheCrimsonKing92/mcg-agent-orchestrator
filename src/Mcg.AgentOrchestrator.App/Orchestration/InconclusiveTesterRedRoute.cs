using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// An inconclusive label does not erase current, executed pre-Tester failure evidence.
internal static class InconclusiveTesterRedRoute
{
    internal static AttributedFailure? ReadPreTesterRed(Goal goal, TaskSpec tester, string? candidateSha)
    {
        if (string.IsNullOrWhiteSpace(candidateSha) || tester.RequiredRole != AgentRole.Tester ||
            tester.Status != WorkTaskStatus.Failed || tester.LastVerification is not { } verification ||
            DispatchFailureClassifier.Classify(tester, verification).Kind != DispatchOutcomeKind.VerificationInconclusive)
            return null;

        var entry = PreTesterEvidenceIndexLines.Latest(goal, tester.Id, candidateSha);
        if (entry is not { Outcome: "red", FailingTests.Count: > 0 }) return null;
        if (PreTesterOutOfDiffRerunMarker.Latest(goal, tester.Id, entry.CandidateSha, entry.ReceiptId)
            is { Verdict: "non-blocking" }) return null;
        var classes = FindingEvidenceExecutedCoverage.ExecutedSelections(entry.Selections, entry.NotRun)
            .Select(selection => selection[(selection.LastIndexOf(':') + 1)..].Split('.')[^1])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var identity in entry.FailingTests)
        {
            var parameterStart = identity.IndexOf('(');
            var parts = (parameterStart < 0 ? identity : identity[..parameterStart]).Split('.');
            if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[^2]) || !classes.Contains(parts[^2]))
                return null;
        }
        return new("pre-tester-receipt", entry.ReceiptId, entry.CandidateSha, entry.FailingTests);
    }

    internal static AttributedFailureRouteDecision Decide(
        Goal goal, FailedGoalRecoveryDecision decision, string? candidateSha)
    {
        if (!decision.DiscriminatingEvidence.StartsWith("verification-inconclusive", StringComparison.Ordinal))
            return AttributedFailureRouteDecision.Decline("not-verification-inconclusive");
        var tester = goal.Tasks.FirstOrDefault(task => task.Id == decision.Identity.TaskId);
        return tester is null
            ? AttributedFailureRouteDecision.Decline("no-target-task")
            : AttributedFailureDeveloperRoute.Decide(goal, ReadPreTesterRed(goal, tester, candidateSha), candidateSha);
    }
}
