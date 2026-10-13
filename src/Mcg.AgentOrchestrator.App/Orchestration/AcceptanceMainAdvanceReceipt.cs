using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class AcceptanceMainAdvanceReceipt
{
    internal static string BuildRevalidationHoldIdentity(
        ConductorParallelAcceptanceCandidate candidate,
        Func<Goal, (string? BranchHeadSha, string? MainHeadSha)> resolveHeads)
    {
        try
        {
            var current = resolveHeads(candidate.Goal);
            return $"acceptance-revalidation:" +
                $"{current.BranchHeadSha ?? candidate.BranchHeadSha ?? "unknown"}:" +
                $"{current.MainHeadSha ?? candidate.MainHeadSha ?? "unknown"}";
        }
        catch
        {
            return $"acceptance-revalidation:{candidate.CandidateKey}";
        }
    }

    internal static string FormatCarryForward(
        AcceptanceMainAdvanceDisposition disposition,
        AcceptanceVerificationSummary acceptance,
        string currentBranchSha,
        string currentMainSha) =>
        $"{disposition.FormatReceipt()} " +
        $"verifiedBranch={Bound(acceptance.BranchHeadSha, 40)} " +
        $"verifiedMain={Bound(acceptance.MainHeadSha, 40)} " +
        $"currentBranch={Bound(currentBranchSha, 40)} currentMain={Bound(currentMainSha, 40)}";

    internal static string Bound(string? value, int maxLength)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }
}
