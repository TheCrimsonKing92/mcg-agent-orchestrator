namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleGateCandidateRerunEvidence
{
    internal static bool Covers(OwnerConsoleGateLane failed, OwnerConsoleGateLane rerun,
        OwnerConsoleGateTrxOutcomes? original, OwnerConsoleGateTrxOutcomes? results) =>
        rerun.EvidenceKind == "candidate-rerun" &&
        string.IsNullOrWhiteSpace(rerun.ReproductionMemberId) &&
        failed.TreeRevision is { Length: 40 or 64 } tree && tree.All(Uri.IsHexDigit) && tree == rerun.TreeRevision &&
        results?.AllPassed == true && original is { Available: true, FailureCount: > 0 } &&
        original.FailureTestNames.All(name => results.PassedTestNames.Contains(name, StringComparer.Ordinal));
}
