using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Persist and route a positively attributed candidate through the existing failure seam.
internal static class MergeTrainAttributedFailure
{
    internal static Action Prepare(
        MergeTrainReceipt receipt, MergeTrainMemberBinding member, Goal goal, string source,
        int memberCount, List<MergeTrainEjection> ejections, Action<MergeTrainEjection> recordEjection,
        Action<Goal, IReadOnlyList<string>, string?, string?, IReadOnlyList<AcceptanceCheckAttribution>?, string?> recordFailure,
        Func<GoalId, string, bool> routeFailure, IReadOnlyList<AcceptanceTrxFailure>? reproducedFailures = null)
    {
        Action complete = () => Apply(receipt, member, goal, source, memberCount, ejections,
            recordEjection, recordFailure, routeFailure, reproducedFailures);
        // Pairs finish with ordinary fallback. Larger trains must first obtain the
        // bisect receipt, keeping all members Verified when its gate is deferred.
        if (memberCount == 2) complete();
        return complete;
    }

    internal static void Apply(
        MergeTrainReceipt receipt, MergeTrainMemberBinding member, Goal goal, string source,
        int memberCount, List<MergeTrainEjection> ejections, Action<MergeTrainEjection> recordEjection,
        Action<Goal, IReadOnlyList<string>, string?, string?, IReadOnlyList<AcceptanceCheckAttribution>?, string?> recordFailure,
        Func<GoalId, string, bool> routeFailure, IReadOnlyList<AcceptanceTrxFailure>? reproducedFailures = null)
    {
        var classes = reproducedFailures is null ? MergeTrainReachAttribution.ReadFailingClasses(receipt.GateTestResultPaths) :
            reproducedFailures.Select(failure => AcceptanceTestSourceResolver.ExtractClassName(failure.TestName!))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (classes is null) return;
        var reason = $"merge train RED attributed ({source}) receipt {receipt.ReceiptId}: {string.Join(", ", classes)}";
        if (reproducedFailures is not null)
            reason = $"merge train RED attributed ({source}) receipt {receipt.ReceiptId}: " +
                string.Join(", ", reproducedFailures.Select(failure => failure.TestName).Distinct(StringComparer.Ordinal)) +
                Environment.NewLine + string.Join(Environment.NewLine, reproducedFailures.Select(failure =>
                    $"reproduced {failure.TestName}: {BoundMessage(failure.Message)}"));
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

        // Reproduction records all ejections before routing; pairs retain ordinary fallback.
        if (memberCount != 2 || ejections.Any(item => item.GoalId == member.GoalId &&
            item.Reason == MergeTrainEjectionReason.RedAttributedMember)) return;
        var ejection = new MergeTrainEjection(member.GoalId, MergeTrainEjectionReason.RedAttributedMember,
            [], $"Dropped after RED receipt {receipt.ReceiptId}.");
        recordEjection(ejection);
        ejections.Add(ejection);
    }

    internal static string BoundMessage(string? message)
    {
        var text = (message ?? "").Replace('\r', ' ').Replace('\n', ' ');
        return text.Length <= 2000 ? text : text[..2000];
    }
}
