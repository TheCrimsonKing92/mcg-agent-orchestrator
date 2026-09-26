using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum ConductorSpeculativeUpstreamExclusionReason
{
    LiveAcceptanceAttempt,
    CohortGateMember,
    DependencyHold,
    PersistedAcceptanceEscalation,
    VerificationIncomplete,
    LifecycleRetired,
    OtherUpstreamFilter
}

internal sealed record ConductorSpeculativeUpstreamExclusion(
    GoalId GoalId,
    ConductorSpeculativeUpstreamExclusionReason Reason);

internal sealed record ConductorSpeculativeCohortReceiptContext(
    IReadOnlyList<ConductorSpeculativeUpstreamExclusion> UpstreamExclusions,
    int? OccupiedWidth = null,
    IReadOnlyList<string>? Occupants = null);

internal sealed partial record ConductorSpeculativeAcceptancePlan
{
    internal string FormatReceipt(int tick, ConductorSpeculativeCohortReceiptContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.UpstreamExclusions.Count == 0 && context.OccupiedWidth is null)
        {
            return FormatReceipt(tick);
        }

        const int maxRenderedOutcomes = 8;
        const int maxReceiptLength = 1024;
        var members = Cohorts.Count == 0
            ? "none"
            : string.Join(',', Cohorts[0].Members.Select(member => Prefix(member.GoalId)));
        var capacity = context.OccupiedWidth is { } width
            ? $" capacity=AcceptanceWidthOccupied(width={width},occupants={Bound(string.Join('+', context.Occupants ?? []))})"
            : string.Empty;

        // Keep the plan-level cause and proposed members ahead of all bounded outcomes.
        var deferrals = Dispositions.OfType<ConductorSpeculativeAcceptanceDisposition.Deferred>()
            .Select(item => (Kind: "deferred", Token: $"{Prefix(item.GoalId)}:{item.Reason}"));
        var exclusions = Dispositions.OfType<ConductorSpeculativeAcceptanceDisposition.Excluded>()
            .Select(item => (Kind: "exclusion", Token: $"{Prefix(item.GoalId)}:{Render(item.Evidence)}"));
        var upstream = context.UpstreamExclusions
            .Select(item => (Kind: "exclusion", Token: $"{Prefix(item.GoalId)}:{item.Reason}"));
        var outcomes = deferrals.Concat(exclusions).Concat(upstream).ToArray();
        var renderedCount = Math.Min(maxRenderedOutcomes, outcomes.Length);

        string Compose(int count)
        {
            var shown = outcomes.Take(count).ToArray();
            var shownExclusions = shown.Where(item => item.Kind == "exclusion").Select(item => item.Token).ToArray();
            var shownDeferrals = shown.Where(item => item.Kind == "deferred").Select(item => item.Token).ToArray();
            return $"SPECULATIVE_COHORT_PLAN tick={tick} advisory=true ready={ReadyCandidateCount} members={members}{capacity} " +
                $"exclusions={(shownExclusions.Length == 0 ? "none" : string.Join(',', shownExclusions))} " +
                $"deferred={(shownDeferrals.Length == 0 ? "none" : string.Join(',', shownDeferrals))} " +
                $"omitted={outcomes.Length - count}";
        }

        var receipt = Compose(renderedCount);
        while (receipt.Length > maxReceiptLength && renderedCount > 0)
        {
            receipt = Compose(--renderedCount);
        }

        if (receipt.Length <= maxReceiptLength)
        {
            return receipt;
        }

        const string suffix = " truncated=true";
        return receipt[..(maxReceiptLength - suffix.Length)] + suffix;
    }
}
