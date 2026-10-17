using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Persist and route a positively attributed candidate through the existing failure seam.
internal static class MergeTrainAttributedFailure
{
    internal static void Apply(
        MergeTrainReceipt receipt, MergeTrainMemberBinding member, Goal goal, string source,
        int memberCount, List<MergeTrainEjection> ejections, Action<MergeTrainEjection> recordEjection,
        Action<Goal, IReadOnlyList<string>, string?, string?, IReadOnlyList<AcceptanceCheckAttribution>?, string?> recordFailure,
        Func<GoalId, string, bool> routeFailure)
    {
        var classes = MergeTrainReachAttribution.ReadFailingClasses(receipt.GateTestResultPaths);
        if (classes is null) return;
        var reason = $"merge train RED attributed ({source}) receipt {receipt.ReceiptId}: {string.Join(", ", classes)}";
        if (goal.Status != GoalStatus.AcceptanceFailed ||
            goal.LatestAcceptanceFailure?.BranchHeadSha != member.CandidateRevision)
        {
            if (goal.Status != GoalStatus.Verified)
                throw new InvalidOperationException("Attributed merge train failure requires a Verified candidate.");
            recordFailure(goal, classes, member.CandidateRevision, receipt.Identity.ObservedMainRevision,
                classes.Select(name => new AcceptanceCheckAttribution(name, AcceptanceFailureOrigin.Introduced, reason)).ToArray(), null);
            if (!routeFailure(goal.Id, reason))
                throw new InvalidOperationException("Recorded merge train acceptance failure could not route its Verified member.");
        }

        // Larger trains use the existing bisect ejection; pairs retain ordinary fallback.
        if (memberCount != 2) return;
        var ejection = new MergeTrainEjection(member.GoalId, MergeTrainEjectionReason.RedAttributedMember,
            [], $"Dropped after RED receipt {receipt.ReceiptId}.");
        recordEjection(ejection);
        ejections.Add(ejection);
    }
}
