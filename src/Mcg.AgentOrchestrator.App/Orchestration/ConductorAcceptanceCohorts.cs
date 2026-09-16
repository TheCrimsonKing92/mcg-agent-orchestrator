using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum ConductorAcceptanceCohortPairExclusionReason
{
    UpstreamExcluded,
    DuplicateGoal,
    IncompleteLandingScope,
    DocsTreeOnlyCandidate,
    IncompleteResourceKeys,
    MainRevisionMismatch,
    LandingPathOverlap,
    SerializedResourceOverlap,
    SuppressedInteraction
}

internal sealed record ConductorAcceptanceCohortPairExclusion(
    GoalId FirstGoalId,
    GoalId SecondGoalId,
    ConductorAcceptanceCohortPairExclusionReason Reason,
    string? Evidence = null);

internal sealed record ConductorAcceptanceCohortSelection(
    IReadOnlyList<GateReadyCandidateProjection> Members,
    IReadOnlyList<ConductorAcceptanceCohortPairExclusion> Exclusions)
{
    internal IReadOnlyList<AcceptanceCohortMemberBinding> BindMembers() =>
        Members.Select(member => new AcceptanceCohortMemberBinding(
            member.GoalId,
            member.BranchRevision,
            member.CandidateRevision,
            member.LandingPaths,
            member.ResourceKeys,
            member.ChangeRiskTier,
            member.AutoPromotionDisposition,
            member.MergeEvidence.Status.ToString(),
            member.MergeEvidence.Reason.ToString())).ToArray();
}

internal sealed record ConductorAcceptanceCohortSelectionResult(
    ConductorAcceptanceCohortSelection? Selection,
    IReadOnlyList<ConductorAcceptanceCohortPairExclusion> Exclusions);

internal sealed record ConductorAcceptanceCohortRunResult(
    AcceptanceCohortReceipt? Receipt,
    IReadOnlyDictionary<string, ConductorAdvanceResult> MemberResults,
    string Detail);

// What a cohort run reports to the conduct tick: the ordinary run result, plus the typed fault when the
// run ended in a cohort gate fault. The fault rides beside the result rather than inside it so the shared
// ConductorAcceptanceCohortRunResult shape stays as it is for every other cohort caller.
internal sealed record ConductorAcceptanceCohortRunOutcome(
    ConductorAcceptanceCohortRunResult Run,
    ConductorAcceptanceCohortGateFault? Fault = null);

// A background acceptance cohort gate that ended in an exception, carried as data so the conduct tick
// can classify it and hold or escalate the members instead of receiving a throw across the tick boundary.
internal sealed record ConductorAcceptanceCohortGateFault(
    string MemberPairKey,
    string PairFingerprint,
    Exception Fault)
{
    internal string FaultType => Fault.GetType().Name;

    internal string Message => BoundFaultMessage(Fault.Message);

    private static string BoundFaultMessage(string value)
    {
        var singleLine = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        return singleLine.Length <= 256 ? singleLine : singleLine[..256];
    }
}

internal static class ConductorAcceptanceCohortSelector
{
    internal const int CohortSize = 2;

    internal static ConductorAcceptanceCohortSelectionResult Select(
        IReadOnlyList<ConductorSpeculativeAcceptanceCandidate> orderedCandidates,
        GoalId? forcedCandidate = null,
        IReadOnlySet<string>? suppressedPairFingerprints = null)
    {
        ArgumentNullException.ThrowIfNull(orderedCandidates);
        var ready = new List<GateReadyCandidateProjection>(orderedCandidates.Count);
        var exclusions = new List<ConductorAcceptanceCohortPairExclusion>();
        foreach (var candidate in orderedCandidates)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            if (candidate.ProjectionResult is not GateReadyCandidateProjectionResult.Ready projected)
            {
                var evidence = candidate.ProjectionResult is GateReadyCandidateProjectionResult.Excluded excluded
                    ? excluded.Reason.ToString()
                    : candidate.ProjectionResult.GetType().Name;
                exclusions.Add(new ConductorAcceptanceCohortPairExclusion(
                    candidate.GoalId,
                    candidate.GoalId,
                    ConductorAcceptanceCohortPairExclusionReason.UpstreamExcluded,
                    evidence));
                continue;
            }
            if (projected.Projection.GoalId != candidate.GoalId)
            {
                throw new InvalidOperationException(
                    $"Cohort candidate {candidate.GoalId.Value} does not match its Ready projection {projected.Projection.GoalId.Value}.");
            }
            ready.Add(projected.Projection);
        }

        var forcedCandidateIsReady = false;
        if (forcedCandidate is not null)
        {
            var forcedIndex = ready.FindIndex(candidate => candidate.GoalId == forcedCandidate);
            if (forcedIndex < 0)
            {
                return new ConductorAcceptanceCohortSelectionResult(
                    Selection: null,
                    Array.AsReadOnly((exclusions.Count > 0
                        ? exclusions
                        : [new ConductorAcceptanceCohortPairExclusion(
                            forcedCandidate,
                            forcedCandidate,
                            ConductorAcceptanceCohortPairExclusionReason.UpstreamExcluded,
                            "forced candidate has no current Ready projection")]).ToArray()));
            }
            else
            {
                var forced = ready[forcedIndex];
                ready.RemoveAt(forcedIndex);
                ready.Insert(0, forced);
                forcedCandidateIsReady = true;
            }
        }

        var firstCandidateLimit = forcedCandidateIsReady ? Math.Min(1, ready.Count - 1) : ready.Count - 1;
        for (var firstIndex = 0; firstIndex < firstCandidateLimit; firstIndex++)
        {
            for (var secondIndex = firstIndex + 1; secondIndex < ready.Count; secondIndex++)
            {
                var first = ready[firstIndex];
                var second = ready[secondIndex];
                var exclusion = FindExclusion(first, second, suppressedPairFingerprints);
                if (exclusion is null)
                {
                    var frozenExclusions = Array.AsReadOnly(exclusions.ToArray());
                    return new ConductorAcceptanceCohortSelectionResult(
                        new ConductorAcceptanceCohortSelection(
                            Array.AsReadOnly([first, second]),
                            frozenExclusions),
                        frozenExclusions);
                }
                exclusions.Add(exclusion);
            }
        }

        return new ConductorAcceptanceCohortSelectionResult(
            Selection: null,
            Array.AsReadOnly(exclusions.ToArray()));
    }

    internal static string PairFingerprint(GateReadyCandidateProjection first, GateReadyCandidateProjection second) =>
        $"{first.GoalId.Value}:{first.CandidateRevision}:{second.GoalId.Value}:{second.CandidateRevision}:{first.MainRevision}";

    private static ConductorAcceptanceCohortPairExclusion? FindExclusion(
        GateReadyCandidateProjection first,
        GateReadyCandidateProjection second,
        IReadOnlySet<string>? suppressedPairFingerprints)
    {
        if (first.GoalId == second.GoalId)
        {
            return Excluded(first, second, ConductorAcceptanceCohortPairExclusionReason.DuplicateGoal);
        }
        if (first.LandingPaths.Count == 0 || second.LandingPaths.Count == 0)
        {
            return Excluded(first, second, ConductorAcceptanceCohortPairExclusionReason.IncompleteLandingScope);
        }
        var firstDisposition = GoalAcceptanceVerifier.ClassifyDotnetShardDisposition(first.LandingPaths);
        var secondDisposition = GoalAcceptanceVerifier.ClassifyDotnetShardDisposition(second.LandingPaths);
        if (firstDisposition == DotnetShardDisposition.DocsTreeOnlyCandidate ||
            secondDisposition == DotnetShardDisposition.DocsTreeOnlyCandidate)
        {
            var docsGoalId = firstDisposition == DotnetShardDisposition.DocsTreeOnlyCandidate
                ? first.GoalId
                : second.GoalId;
            return Excluded(
                first,
                second,
                ConductorAcceptanceCohortPairExclusionReason.DocsTreeOnlyCandidate,
                $"goal={docsGoalId.Value}; disposition=docs-tree-only-candidate");
        }
        if (first.ResourceKeys.Count == 0 || second.ResourceKeys.Count == 0)
        {
            return Excluded(first, second, ConductorAcceptanceCohortPairExclusionReason.IncompleteResourceKeys);
        }
        if (!first.MainRevision.Equals(second.MainRevision, StringComparison.Ordinal))
        {
            return Excluded(
                first,
                second,
                ConductorAcceptanceCohortPairExclusionReason.MainRevisionMismatch,
                $"{first.MainRevision}:{second.MainRevision}");
        }
        foreach (var firstPath in first.LandingPaths)
        {
            foreach (var secondPath in second.LandingPaths)
            {
                if (RepositoryPathOverlap.Overlaps(firstPath, secondPath))
                {
                    return Excluded(
                        first,
                        second,
                        ConductorAcceptanceCohortPairExclusionReason.LandingPathOverlap,
                        $"{firstPath}:{secondPath}");
                }
            }
        }
        var sharedResource = first.ResourceKeys.FirstOrDefault(resource =>
            second.ResourceKeys.Contains(resource, StringComparer.OrdinalIgnoreCase));
        if (sharedResource is not null)
        {
            return Excluded(
                first,
                second,
                ConductorAcceptanceCohortPairExclusionReason.SerializedResourceOverlap,
                sharedResource);
        }
        if (suppressedPairFingerprints?.Contains(PairFingerprint(first, second)) == true)
        {
            return Excluded(first, second, ConductorAcceptanceCohortPairExclusionReason.SuppressedInteraction);
        }
        return null;
    }

    private static ConductorAcceptanceCohortPairExclusion Excluded(
        GateReadyCandidateProjection first,
        GateReadyCandidateProjection second,
        ConductorAcceptanceCohortPairExclusionReason reason,
        string? evidence = null) => new(first.GoalId, second.GoalId, reason, evidence);
}

internal static class ConductorAcceptanceCohortAttribution
{
    internal static AcceptanceCohortAttributionOutcome Classify(
        AcceptanceCohortGateOutcome first,
        AcceptanceCohortGateOutcome second)
    {
        if (first is AcceptanceCohortGateOutcome.InfrastructureFailure or AcceptanceCohortGateOutcome.Invalidated ||
            second is AcceptanceCohortGateOutcome.InfrastructureFailure or AcceptanceCohortGateOutcome.Invalidated)
        {
            return AcceptanceCohortAttributionOutcome.Indeterminate;
        }
        if (first == AcceptanceCohortGateOutcome.Failed && second == AcceptanceCohortGateOutcome.Passed)
        {
            return AcceptanceCohortAttributionOutcome.FirstMemberFailed;
        }
        if (first == AcceptanceCohortGateOutcome.Passed && second == AcceptanceCohortGateOutcome.Failed)
        {
            return AcceptanceCohortAttributionOutcome.SecondMemberFailed;
        }
        if (first == AcceptanceCohortGateOutcome.Failed && second == AcceptanceCohortGateOutcome.Failed)
        {
            return AcceptanceCohortAttributionOutcome.BothMembersFailed;
        }
        if (first == AcceptanceCohortGateOutcome.Passed && second == AcceptanceCohortGateOutcome.Passed)
        {
            return AcceptanceCohortAttributionOutcome.InteractionOnly;
        }
        return AcceptanceCohortAttributionOutcome.Indeterminate;
    }
}

internal sealed partial class ConductorDriver
{
    // A background cohort gate that faulted is parked here as a typed fault instead of being rethrown
    // into the conduct tick, and drained by the next RunAcceptanceCohort call for the same member pair.
    private readonly ConcurrentDictionary<string, ConductorAcceptanceCohortGateFault> _cohortGateFaults =
        new(StringComparer.Ordinal);
    // Transient cohort-gate faults counted per member pair. The pair fingerprint moves with the main
    // revision, so counting by fingerprint would reset before the cap and loop forever; the member pair
    // is the stable identity of "this cohort keeps faulting".
    private readonly ConcurrentDictionary<string, int> _cohortGateFaultCounts = new(StringComparer.Ordinal);

    // The conduct tick's entry point. A background cohort gate that ended in an exception is reported here
    // as a typed fault beside the held run result, so the tick classifies the fault instead of receiving a
    // throw from a thread that finished several ticks ago. A parked fault is taken before anything else,
    // including an injected cohort runner, so it can never be lost or turned into a fresh gate start.
    internal ConductorAcceptanceCohortRunOutcome RunAcceptanceCohortForTick(
        ConductorAcceptanceCohortSelection selection,
        IReadOnlyList<Goal> orderedGoals,
        ConductorAutonomyPolicy policy,
        CancellationToken cancellationToken = default,
        Action? onGateAdmitted = null,
        bool runGateInBackground = false)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(orderedGoals);
        return TakeCohortGateFault(selection) is { } fault
            ? new ConductorAcceptanceCohortRunOutcome(
                CohortGateFaulted(selection, orderedGoals, policy, fault),
                fault)
            : new ConductorAcceptanceCohortRunOutcome(
                RunAcceptanceCohort(
                    selection,
                    orderedGoals,
                    policy,
                    cancellationToken,
                    onGateAdmitted,
                    runGateInBackground));
    }

    private void SweepCompletedCohortGateRuns()
    {
        foreach (var pair in _cohortGateRuns)
        {
            if (!pair.Value.Completion.Task.IsCompleted ||
                !_cohortGateRuns.TryRemove(pair.Key, out var completed))
            {
                continue;
            }

            ObserveCohortGateCompletion(pair.Key, completed);
        }
    }

    // Observes this member pair's completed background gate, if any, and takes the fault it parked. Called
    // before the ordinary cohort path so a faulted pair is reported as data instead of being restarted as a
    // fresh gate, and so the exception is never observed by a GetAwaiter().GetResult() on the tick thread.
    private ConductorAcceptanceCohortGateFault? TakeCohortGateFault(
        ConductorAcceptanceCohortSelection selection)
    {
        var memberPairKey = CohortGateMemberPairKey(selection);
        if (_cohortGateRuns.TryGetValue(memberPairKey, out var run) &&
            run.Completion.Task.IsCompleted &&
            _cohortGateRuns.TryRemove(memberPairKey, out var completed))
        {
            ObserveCohortGateCompletion(memberPairKey, completed);
        }

        SweepCompletedCohortGateRuns();
        return _cohortGateFaults.TryRemove(memberPairKey, out var fault) ? fault : null;
    }

    // Observes a completed background cohort gate without rethrowing: a fault is parked as typed data
    // for its member pair, and a clean completion clears that pair's transient-fault count.
    private void ObserveCohortGateCompletion(string memberPairKey, CohortGateRun run)
    {
        if (run.Completion.Task.Exception is { } aggregate)
        {
            _cohortGateFaults[memberPairKey] = new ConductorAcceptanceCohortGateFault(
                memberPairKey,
                run.PairFingerprint,
                aggregate.InnerExceptions.Count == 1 ? aggregate.InnerExceptions[0] : aggregate);
            return;
        }

        _cohortGateFaultCounts.TryRemove(memberPairKey, out _);
    }

    // Publishes an already-completed background cohort gate run for the selection's member pair so the
    // drain path can be exercised without standing up a live gate.
    internal void PublishCompletedCohortGateRunForTests(
        ConductorAcceptanceCohortSelection selection,
        Exception? fault)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (fault is null)
        {
            completion.SetResult();
        }
        else
        {
            completion.SetException(fault);
        }

        _cohortGateRuns[CohortGateMemberPairKey(selection)] = new CohortGateRun(
            _utcNow(),
            selection.Members.Select(member => member.GoalId.Value).ToHashSet(StringComparer.Ordinal),
            ConductorAcceptanceCohortSelector.PairFingerprint(selection.Members[0], selection.Members[1]),
            completion);
    }

    internal static ConductorAcceptanceCohortGateFault CreateCohortGateFault(
        ConductorAcceptanceCohortSelection selection,
        Exception fault) =>
        new(
            CohortGateMemberPairKey(selection),
            ConductorAcceptanceCohortSelector.PairFingerprint(selection.Members[0], selection.Members[1]),
            fault);

    internal int RecordCohortGateTransientFault(string memberPairKey) =>
        _cohortGateFaultCounts.AddOrUpdate(memberPairKey, 1, (_, count) => count + 1);

    internal void ClearCohortGateTransientFaults(string memberPairKey) =>
        _cohortGateFaultCounts.TryRemove(memberPairKey, out _);

    private ConductorAcceptanceCohortRunResult CohortGateFaulted(
        ConductorAcceptanceCohortSelection selection,
        IReadOnlyList<Goal> orderedGoals,
        ConductorAutonomyPolicy policy,
        ConductorAcceptanceCohortGateFault fault)
    {
        var selectedIds = selection.Members.Select(member => member.GoalId).ToHashSet();
        var goals = orderedGoals.Where(goal => selectedIds.Contains(goal.Id)).ToArray();
        var detail =
            $"outcome=gate-fault fingerprint={fault.PairFingerprint} fault={fault.FaultType} " +
            $"detail={BoundCohortDetail(fault.Message)}";
        return new ConductorAcceptanceCohortRunResult(
            Receipt: null,
            goals.ToDictionary(
                goal => goal.Id.Value,
                goal => MakeResult(
                    goal.Id.Value,
                    goal.Id.Value[..8],
                    policy,
                    new ConductorAdvanceOutcome.Held(
                        GoalLifecycleState.Verified,
                        $"Acceptance cohort gate faulted: {detail}")),
                StringComparer.Ordinal),
            detail);
    }
}
