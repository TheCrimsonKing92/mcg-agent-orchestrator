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
    SuppressedInteraction,
    AttributedMember,
    TrainImplicatedMember
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

internal enum ConductorAcceptanceHeldConflictKind
{
    LandingPath,
    SerializedResource,
    UnknownResourceIdentity
}

internal enum ConductorAcceptanceCohortFairnessOutcome
{
    YieldedToRunnableYounger,
    NoRunnableYounger
}

internal sealed record ConductorAcceptanceHeldResource(
    string HolderGoalId,
    IReadOnlyList<string> LandingPaths,
    IReadOnlyList<string> ResourceKeys);

internal sealed record ConductorAcceptanceHeldConflict(
    GoalId CandidateGoalId,
    string HolderGoalId,
    ConductorAcceptanceHeldConflictKind Kind,
    string ConflictKey);

internal sealed record ConductorAcceptanceCohortFairnessContext(
    GoalId BlockedHeadGoalId,
    int OvertakeCount,
    IReadOnlyList<ConductorAcceptanceHeldResource> HeldResources);

internal sealed record ConductorAcceptanceCohortFairnessPriority(
    GoalId GoalId,
    int OvertakeCount);

internal sealed record ConductorAcceptanceCohortFairnessDecision(
    ConductorAcceptanceCohortFairnessOutcome Outcome,
    GoalId BlockedHeadGoalId,
    GoalId? SelectedGoalId,
    ConductorAcceptanceHeldConflict HeadConflict,
    int OvertakeCount,
    IReadOnlyList<ConductorAcceptanceHeldConflict> CandidateConflicts);

internal sealed record ConductorAcceptanceCohortSelectionResult(
    ConductorAcceptanceCohortSelection? Selection,
    IReadOnlyList<ConductorAcceptanceCohortPairExclusion> Exclusions,
    ConductorAcceptanceCohortFairnessDecision? FairnessDecision = null);

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
    IReadOnlySet<string> MemberGoalIds,
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
        IReadOnlySet<string>? suppressedPairFingerprints = null,
        ConductorAcceptanceCohortFairnessContext? fairnessContext = null,
        IReadOnlySet<string>? attributedMemberKeys = null,
        IReadOnlySet<string>? trainImplicatedMemberKeys = null)
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
            if (attributedMemberKeys?.Contains(ConductorAcceptanceCohortAttributedMembers.Key(
                    candidate.GoalId, projected.Projection.CandidateRevision)) == true)
            {
                exclusions.Add(new ConductorAcceptanceCohortPairExclusion(
                    candidate.GoalId, candidate.GoalId,
                    ConductorAcceptanceCohortPairExclusionReason.AttributedMember,
                    $"candidate={projected.Projection.CandidateRevision}"));
                continue;
            }
            if (trainImplicatedMemberKeys?.Contains(ConductorAcceptanceCohortAttributedMembers.Key(
                    candidate.GoalId, projected.Projection.CandidateRevision)) == true)
            {
                exclusions.Add(new ConductorAcceptanceCohortPairExclusion(
                    candidate.GoalId, candidate.GoalId,
                    ConductorAcceptanceCohortPairExclusionReason.TrainImplicatedMember,
                    $"candidate={projected.Projection.CandidateRevision}"));
                continue;
            }
            ready.Add(projected.Projection);
        }

        if (fairnessContext is not null)
        {
            if (forcedCandidate is null || forcedCandidate != fairnessContext.BlockedHeadGoalId)
            {
                throw new InvalidOperationException(
                    "A cohort fairness context must describe the exact forced candidate.");
            }

            var blockedHeadIndex = ready.FindIndex(candidate => candidate.GoalId == forcedCandidate);
            if (blockedHeadIndex >= 0 &&
                FindHeldConflict(ready[blockedHeadIndex], fairnessContext.HeldResources) is { } headConflict)
            {
                var candidateConflicts = new List<ConductorAcceptanceHeldConflict>();
                for (var candidateIndex = blockedHeadIndex + 1; candidateIndex < ready.Count; candidateIndex++)
                {
                    var candidate = ready[candidateIndex];
                    if (FindHeldConflict(candidate, fairnessContext.HeldResources) is { } candidateConflict)
                    {
                        candidateConflicts.Add(candidateConflict);
                        continue;
                    }

                    for (var peerIndex = candidateIndex + 1; peerIndex < ready.Count; peerIndex++)
                    {
                        var peer = ready[peerIndex];
                        if (FindHeldConflict(peer, fairnessContext.HeldResources) is { } peerConflict)
                        {
                            candidateConflicts.Add(peerConflict);
                            continue;
                        }

                        var pairExclusion = FindExclusion(candidate, peer, suppressedPairFingerprints);
                        if (pairExclusion is not null)
                        {
                            exclusions.Add(pairExclusion);
                            continue;
                        }

                        var frozenExclusions = Array.AsReadOnly(exclusions.ToArray());
                        return new ConductorAcceptanceCohortSelectionResult(
                            new ConductorAcceptanceCohortSelection(
                                Array.AsReadOnly([candidate, peer]),
                                frozenExclusions),
                            frozenExclusions,
                            new ConductorAcceptanceCohortFairnessDecision(
                                ConductorAcceptanceCohortFairnessOutcome.YieldedToRunnableYounger,
                                fairnessContext.BlockedHeadGoalId,
                                candidate.GoalId,
                                headConflict,
                                fairnessContext.OvertakeCount,
                                Array.AsReadOnly(candidateConflicts.ToArray())));
                    }
                }

                return new ConductorAcceptanceCohortSelectionResult(
                    Selection: null,
                    Array.AsReadOnly(exclusions.ToArray()),
                    new ConductorAcceptanceCohortFairnessDecision(
                        ConductorAcceptanceCohortFairnessOutcome.NoRunnableYounger,
                        fairnessContext.BlockedHeadGoalId,
                        SelectedGoalId: null,
                        headConflict,
                        fairnessContext.OvertakeCount,
                        Array.AsReadOnly(candidateConflicts.ToArray())));
            }
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

    private static ConductorAcceptanceHeldConflict? FindHeldConflict(
        GateReadyCandidateProjection candidate,
        IReadOnlyList<ConductorAcceptanceHeldResource> heldResources)
    {
        foreach (var holder in heldResources
                     .OrderBy(holder => holder.HolderGoalId, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(holder.HolderGoalId) ||
                holder.ResourceKeys is null ||
                holder.LandingPaths is null ||
                (holder.ResourceKeys.Count == 0 && holder.LandingPaths.Count == 0) ||
                holder.ResourceKeys.Contains(
                    RepositoryLandingScopeNormalization.UnknownAcceptanceScopeResourceKey,
                    StringComparer.OrdinalIgnoreCase))
            {
                return new ConductorAcceptanceHeldConflict(
                    candidate.GoalId,
                    string.IsNullOrWhiteSpace(holder.HolderGoalId) ? "unknown" : holder.HolderGoalId,
                    ConductorAcceptanceHeldConflictKind.UnknownResourceIdentity,
                    RepositoryLandingScopeNormalization.UnknownAcceptanceScopeResourceKey);
            }

            var sharedResource = candidate.ResourceKeys
                .Intersect(holder.ResourceKeys, StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (sharedResource is not null)
            {
                return new ConductorAcceptanceHeldConflict(
                    candidate.GoalId,
                    holder.HolderGoalId,
                    ConductorAcceptanceHeldConflictKind.SerializedResource,
                    sharedResource);
            }

            foreach (var candidatePath in candidate.LandingPaths.Order(StringComparer.OrdinalIgnoreCase))
            {
                foreach (var heldPath in holder.LandingPaths.Order(StringComparer.OrdinalIgnoreCase))
                {
                    if (RepositoryPathOverlap.Overlaps(candidatePath, heldPath))
                    {
                        return new ConductorAcceptanceHeldConflict(
                            candidate.GoalId,
                            holder.HolderGoalId,
                            ConductorAcceptanceHeldConflictKind.LandingPath,
                            $"path:{candidatePath}:{heldPath}");
                    }
                }
            }
        }

        return null;
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

internal sealed partial class ConductorBatchLoop
{
    private (Goal[] Eligible, ConductorSpeculativeAcceptanceCandidate[] Candidates)
        LandPassedAcceptanceCohortsBeforeTrainSelection(
            ConductorDriver driver,
            ConductorAutonomyPolicy policy,
            Goal[] eligible,
            ConductorSpeculativeAcceptanceCandidate[] candidates,
            Dictionary<string, ParallelLandingOutcome> results,
            int tick,
            List<string> changedGoalLines)
    {
        var landed = false;
        foreach (var selection in driver.FindLandablePassedCohortSelections(candidates))
        {
            if (selection.Members.Any(member => results.ContainsKey(member.GoalId.Value)))
            {
                continue;
            }

            var run = driver.RunAcceptanceCohort(selection, eligible, policy, runGateInBackground: true);
            foreach (var member in run.MemberResults)
            {
                var result = member.Value;
                if (run.Detail.StartsWith("outcome=passed", StringComparison.Ordinal))
                {
                    var goal = eligible.Single(goal => goal.Id.Value == member.Key);
                    var recorded = driver.AdvanceOnce(goal, policy);
                    if (recorded.Outcome is not ConductorAdvanceOutcome.Executed
                        { FromState: GoalLifecycleState.Merged })
                    {
                        throw new InvalidOperationException(
                            $"Pre-landed cohort goal {member.Key} did not record its landing.");
                    }
                    result = driver.AdvanceOnce(goal, policy);
                    if (result.Outcome is not ConductorAdvanceOutcome.Executed
                        { FromState: GoalLifecycleState.Recorded } || goal.Status != GoalStatus.Completed)
                    {
                        throw new InvalidOperationException(
                            $"Pre-landed cohort goal {member.Key} did not complete after cleanup.");
                    }
                }
                results[member.Key] = new ParallelLandingOutcome(result, SlotIndex: 0);
            }
            RecordParallelAcceptanceProgress(
                $"ACCEPTANCE_COHORT tick={tick} members={string.Join(',', selection.Members.Select(member => member.GoalId.Value[..8]))} prelanded=true {run.Detail}",
                changedGoalLines);
            // A successful landing advances main, so other receipts probed against the old main
            // must be reconsidered on a later tick.
            if (run.Detail.StartsWith("outcome=passed", StringComparison.Ordinal))
            {
                landed = true;
                break;
            }
        }

        if (landed)
        {
            candidates = candidates
                .Where(candidate => !results.ContainsKey(candidate.GoalId.Value))
                .Select(candidate => new ConductorSpeculativeAcceptanceCandidate(
                    candidate.GoalId,
                    driver.ProjectGateReadyCandidate(
                        eligible.Single(goal => goal.Id == candidate.GoalId), policy)))
                .ToArray();
        }
        return (eligible.Where(goal => !results.ContainsKey(goal.Id.Value)).ToArray(),
            candidates.Where(candidate => !results.ContainsKey(candidate.GoalId.Value)).ToArray());
    }

    private ConductorAcceptanceCohortSelectionResult SelectAndReportAcceptanceCohort(
        IReadOnlyList<ConductorSpeculativeAcceptanceCandidate> candidates,
        IReadOnlyList<ConductorParallelAcceptanceAttempt> liveAttempts,
        ConductorAcceptanceCohortFairnessPriority? fairnessPriority,
        ConductorDriver driver)
    {
        var fairnessContext = fairnessPriority is null
            ? null
            : new ConductorAcceptanceCohortFairnessContext(
                fairnessPriority.GoalId,
                fairnessPriority.OvertakeCount,
                BuildAcceptanceHeldResources(liveAttempts));
        var decision = ConductorAcceptanceCohortSelector.Select(
            candidates,
            fairnessPriority?.GoalId,
            driver.ReadSuppressedGroupedPairs(),
            fairnessContext,
            driver.ReadCohortAttributedMemberKeys(),
            driver.ReadTrainImplicatedMemberKeys());
        EmitAcceptanceCohortFairnessDecision(decision.FairnessDecision);
        return decision;
    }

    private void EmitAcceptanceCohortFairnessDecision(
        ConductorAcceptanceCohortFairnessDecision? decision)
    {
        if (decision is null) return;
        var candidateConflicts = decision.CandidateConflicts.Count == 0
            ? "none"
            : string.Join(',', decision.CandidateConflicts.Select(conflict =>
                $"{conflict.CandidateGoalId.Value}:{conflict.HolderGoalId}:{conflict.Kind}:{SanitizeReason(conflict.ConflictKey)}"));
        EmitProgress(
            $"ACCEPTANCE_COHORT_FAIRNESS outcome={decision.Outcome} " +
            $"blocked={decision.BlockedHeadGoalId.Value} selected={decision.SelectedGoalId?.Value ?? "none"} " +
            $"holder={decision.HeadConflict.HolderGoalId} conflict_kind={decision.HeadConflict.Kind} " +
            $"conflict={SanitizeReason(decision.HeadConflict.ConflictKey)} " +
            $"overtakes={decision.OvertakeCount} candidate_conflicts={candidateConflicts}");
    }

    private void EmitAcceptanceCohortFairnessTransition(CohortAdmissionFairnessTransition? transition)
    {
        if (transition is null) return;
        EmitProgress(
            $"ACCEPTANCE_COHORT_FAIRNESS_TRANSITION oldest={transition.OldestEligibleGoalId.Value} " +
            $"previous={transition.PreviousOvertakeCount} resulting={transition.ResultingOvertakeCount} " +
            $"oldest_admitted={transition.OldestAdmitted} " +
            $"admitted={string.Join(',', transition.AdmittedGoalIds.Select(goalId => goalId.Value))}");
    }

    private static IReadOnlyList<ConductorAcceptanceHeldResource> BuildAcceptanceHeldResources(
        IReadOnlyList<ConductorParallelAcceptanceAttempt> liveAttempts) =>
        liveAttempts
            .Where(attempt => attempt.Outcome == ConductorParallelAcceptanceAttemptOutcome.Running)
            .OrderBy(attempt => attempt.GoalId, StringComparer.Ordinal)
            .ThenBy(attempt => attempt.AttemptId, StringComparer.Ordinal)
            .Select(attempt =>
            {
                var scope = RepositoryLandingScopeNormalization.Normalize(
                    attempt.ScopePaths ?? [],
                    reserveUnknownScope: true);
                return new ConductorAcceptanceHeldResource(
                    attempt.GoalId,
                    scope.ConflictPaths,
                    scope.ResourceKeys);
            })
            .ToArray();

    // The infrastructure faults the background attempt path treats as transient (IsRetryableAcceptanceRun).
    // A cohort gate that fails this way is retried on a later tick, not turned into a candidate verdict.
    private static bool IsTransientCohortGateFault(Exception exception) =>
        exception is AcceptanceInfrastructureDeferredException or
            AcceptanceGateEngineException or
            DotnetBuildSlotsBusyException or
            BuildLockBlockedException or
            OperationCanceledException;

    private static (string Outcome, string Reason) DescribeAcceptanceCohortGateFault(
        ConductorAcceptanceCohortGateFault fault) =>
        ("gate-fault", $" fault={fault.FaultType} detail={SanitizeReason(fault.Message)}");

    // Counts a transient fault against the member pair the way CompleteParallelAcceptanceRun counts a
    // single candidate's transient failures: hold and retry below the cap, escalate both members at it.
    // A fault outside that class is not retryable and escalates immediately rather than holding stale.
    private static ConductorAcceptanceCohortRunResult ResolveFaultedAcceptanceCohort(
        ConductorDriver driver,
        ConductorAutonomyPolicy policy,
        IReadOnlyList<Goal> cohortEligible,
        ConductorAcceptanceCohortRunResult cohortRun,
        ConductorAcceptanceCohortGateFault fault,
        int tick,
        List<string> changedGoalLines)
    {
        var protectedBoundary = IsProtectedBoundaryRegistrationFault(fault.Fault);
        var transient = !protectedBoundary && IsTransientCohortGateFault(fault.Fault);
        var failureCount = transient ? driver.RecordCohortGateTransientFault(fault.MemberPairKey) : 0;
        var escalate = !transient || failureCount >= ParallelAcceptanceTransientFailureCap;
        var classification = protectedBoundary ? "terminal" : transient ? "transient" : "non-transient";
        var memberResults = new Dictionary<string, ConductorAdvanceResult>(StringComparer.Ordinal);
        foreach (var goal in cohortEligible.Where(goal => fault.MemberGoalIds.Contains(goal.Id.Value)))
        {
            memberResults[goal.Id.Value] = escalate
                ? EscalateParallelAcceptanceSafely(
                    driver,
                    goal,
                    policy,
                    $"infrastructure-hold acceptance cohort gate fault ({classification} " +
                    $"{failureCount}/{ParallelAcceptanceTransientFailureCap}): " +
                    $"{fault.FaultType}: {SanitizeReason(fault.Message)}")
                : ParallelAcceptanceHeld(
                    goal,
                    policy,
                    $"Acceptance cohort gate fault ({failureCount}/{ParallelAcceptanceTransientFailureCap}); " +
                    $"retry on next conduct tick. {fault.FaultType}: {fault.Message}");
            RecordParallelAcceptanceProgress(
                $"ACCEPTANCE_COHORT tick={tick} goal={goal.Id.Value[..8]} result={(escalate ? "escalated" : "held")} " +
                $"fault={fault.FaultType} classification={classification} " +
                $"failures={failureCount}/{ParallelAcceptanceTransientFailureCap} " +
                $"fingerprint={fault.PairFingerprint} detail={SanitizeReason(fault.Message)}",
                changedGoalLines);
        }

        if (escalate)
        {
            driver.ClearCohortGateTransientFaults(fault.MemberPairKey);
        }

        return cohortRun with { MemberResults = memberResults };
    }
}

internal sealed partial class ConductorDriver
{
    internal IReadOnlyList<ConductorAcceptanceCohortSelection> FindLandablePassedCohortSelections(
        IReadOnlyList<ConductorSpeculativeAcceptanceCandidate> candidates)
    {
        if (_cohortAcceptanceStore is null)
        {
            return [];
        }

        var ready = candidates
            .Where(candidate => candidate.ProjectionResult is GateReadyCandidateProjectionResult.Ready)
            .ToDictionary(candidate => candidate.GoalId,
                candidate => ((GateReadyCandidateProjectionResult.Ready)candidate.ProjectionResult).Projection);
        var claimed = new HashSet<GoalId>();
        var selections = new List<ConductorAcceptanceCohortSelection>();
        foreach (var candidate in candidates)
        {
            if (!ready.TryGetValue(candidate.GoalId, out var first) || claimed.Contains(candidate.GoalId))
            {
                continue;
            }

            foreach (var receipt in _cohortAcceptanceStore.ReadPassedReceiptsForGoal(candidate.GoalId))
            {
                if (receipt.Identity.Members.Count != ConductorAcceptanceCohortSelector.CohortSize ||
                    !string.Equals(receipt.Identity.ObservedMainRevision, first.MainRevision, StringComparison.Ordinal) ||
                    receipt.Identity.Members.Any(member =>
                        claimed.Contains(member.GoalId) ||
                        !ready.TryGetValue(member.GoalId, out var live) ||
                        !string.Equals(member.CandidateRevision, live.CandidateRevision, StringComparison.Ordinal)))
                {
                    continue;
                }

                var pair = receipt.Identity.Members
                    .Select(member => candidates.Single(candidate => candidate.GoalId == member.GoalId))
                    .ToArray();
                var selection = ConductorAcceptanceCohortSelector.Select(
                    pair, suppressedPairFingerprints: ReadSuppressedCohortPairs()).Selection;
                if (selection is null)
                {
                    continue;
                }
                if (!HasCurrentPassedCohortIdentity(selection, receipt))
                {
                    continue;
                }

                selections.Add(selection);
                foreach (var member in selection.Members)
                {
                    claimed.Add(member.GoalId);
                }
                break;
            }
        }
        return selections;
    }

    private bool HasCurrentPassedCohortIdentity(
        ConductorAcceptanceCohortSelection selection,
        AcceptanceCohortReceipt receipt)
    {
        if (_cohortWorkspace is null || _cohortAcceptanceVerifier is null)
        {
            return false;
        }

        var bindings = selection.BindMembers();
        try
        {
            using var integration = GoalWorktrees.CreateAcceptanceCohortWorkspace(
                _cohortWorkspace.ExecutionDirectory,
                selection.Members[0].MainRevision,
                bindings, _integrationBranch,
                _cohortCleanupHooks);
            var manifest = _cohortAcceptanceVerifier.ComputeEffectivePlanIdentity(
                integration.Path,
                bindings.SelectMany(member => member.LandingPaths)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
            return string.Equals(
                AcceptanceCohortIdentity.Create(bindings, selection.Members[0].MainRevision,
                    integration.TreeRevision, manifest).Value,
                receipt.Identity.Value,
                StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is AcceptanceCohortMaterializationException or
            IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    internal ConductorAcceptanceCohortFairnessPriority? SelectForcedCohortCandidate(
        IReadOnlyList<Goal> orderedGoals)
    {
        if (_cohortAcceptanceStore is null) return null;
        foreach (var goal in orderedGoals)
        {
            var count = _cohortAcceptanceStore.ReadOvertakeCount(goal.Id);
            if (count >= ConductorBatchLoop.ParallelAcceptanceBoundedOvertakeLimit)
            {
                return new ConductorAcceptanceCohortFairnessPriority(goal.Id, count);
            }
        }

        return null;
    }

    internal CohortAdmissionFairnessTransition RecordCohortAdmissionFairness(
        IReadOnlyList<Goal> orderedGoals,
        ConductorAcceptanceCohortSelection selection)
    {
        if (_cohortAcceptanceStore is null)
        {
            throw new InvalidOperationException("Cohort admission fairness store is unavailable.");
        }
        if (orderedGoals.Count == 0)
        {
            throw new InvalidOperationException("Cohort admission fairness requires an ordered eligible goal.");
        }
        var admitted = selection.Members.Select(member => member.GoalId).ToHashSet();
        var oldest = orderedGoals[0].Id;
        return _cohortAcceptanceStore.ApplyAdmissionFairness(admitted, oldest);
    }

    // A background cohort gate that faulted is parked here as a typed fault instead of being rethrown
    // into the conduct tick, and drained by the next RunAcceptanceCohort call for the same member pair.
    private readonly ConcurrentDictionary<string, ConductorAcceptanceCohortGateFault> _cohortGateFaults =
        new(StringComparer.Ordinal);
    // Transient cohort-gate faults counted per member pair. The pair fingerprint moves with the main
    // revision, so counting by fingerprint would reset before the cap and loop forever; the member pair
    // is the stable identity of "this cohort keeps faulting".
    private readonly ConcurrentDictionary<string, int> _cohortGateFaultCounts = new(StringComparer.Ordinal);
    private readonly object _cohortGateRegistrationSync = new();

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
        return TakeNextCohortGateFault() is { } fault
            ? new ConductorAcceptanceCohortRunOutcome(
                CohortGateFaulted(orderedGoals, policy, fault),
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
        ObserveOwnedGroupedGateAttempts();
        foreach (var pair in _cohortGateRuns)
        {
            if (!pair.Value.Completion.Task.IsCompleted ||
                !_cohortGateRuns.TryRemove(pair.Key, out var completed))
            {
                continue;
            }

            ObserveCohortGateCompletion(pair.Key, completed);
        }
        RestoreRunningGroupedGateAttempts();
    }

    private bool TryGetActiveCohortGateRun(
        IReadOnlySet<string> candidateMemberGoalIds,
        out CohortGateRun? activeRun)
    {
        activeRun = _cohortGateRuns
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Value)
            .FirstOrDefault(run =>
                !run.Completion.Task.IsCompleted &&
                candidateMemberGoalIds.Overlaps(run.MemberGoalIds));
        return activeRun is not null;
    }

    private bool TryRegisterCohortGateRun(
        string memberPairKey,
        CohortGateRun run,
        out CohortGateRun? blockingRun,
        Action? afterSweepForTests = null)
    {
        lock (_cohortGateRegistrationSync)
        {
            while (true)
            {
                SweepCompletedCohortGateRuns();
                afterSweepForTests?.Invoke();
                afterSweepForTests = null;
                if (TryGetActiveCohortGateRun(run.MemberGoalIds, out blockingRun))
                {
                    return false;
                }

                if (_cohortGateRuns.TryAdd(memberPairKey, run))
                {
                    blockingRun = null;
                    return true;
                }

                if (!_cohortGateRuns.TryGetValue(memberPairKey, out var incumbent) ||
                    incumbent.Completion.Task.IsCompleted)
                {
                    continue;
                }

                throw new InvalidOperationException(
                    $"Cohort gate registration for '{memberPairKey}' lost ownership without an active overlapping run.");
            }
        }
    }

    internal IReadOnlySet<string> GetActiveCohortGateMemberGoalIds(
        Action<IReadOnlySet<string>, string>? observeActiveRun = null)
    {
        SweepCompletedCohortGateRuns();
        var activeRuns = _cohortGateRuns.Values
            .Where(run => !run.Completion.Task.IsCompleted)
            .OrderBy(run => run.PairFingerprint, StringComparer.Ordinal)
            .ToArray();
        foreach (var run in activeRuns)
        {
            observeActiveRun?.Invoke(run.MemberGoalIds, FormatCohortGateInFlightDetail(run, _utcNow()));
        }

        return activeRuns
            .SelectMany(run => run.MemberGoalIds)
            .ToHashSet(StringComparer.Ordinal);
    }

    internal bool TryRegisterCohortGateRunForTests(
        ConductorAcceptanceCohortSelection selection,
        TaskCompletionSource completion,
        Action? afterSweep = null)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(completion);
        var run = new CohortGateRun(
            _utcNow(),
            selection.Members.Select(member => member.GoalId.Value).ToHashSet(StringComparer.Ordinal),
            ConductorAcceptanceCohortSelector.PairFingerprint(selection.Members[0], selection.Members[1]),
            completion);
        return TryRegisterCohortGateRun(CohortGateMemberPairKey(selection), run, out _, afterSweep);
    }

    private static string FormatCohortGateInFlightDetail(CohortGateRun run, DateTimeOffset now) =>
        $"outcome=inflight owner={string.Join('+', run.MemberGoalIds.OrderBy(id => id, StringComparer.Ordinal))} " +
        $"fingerprint={run.PairFingerprint} elapsed_ms={Math.Max(0L, (long)(now - run.StartedAt).TotalMilliseconds)}";

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

    // A completed run belongs to the pair that started it, not whichever pair happens to be selected on the
    // next tick. Drain by stable key so every parked fault is surfaced and counted for its own
    // members before the newly selected pair can start another gate.
    private ConductorAcceptanceCohortGateFault? TakeNextCohortGateFault()
    {
        SweepCompletedCohortGateRuns();
        foreach (var memberPairKey in _cohortGateFaults.Keys.OrderBy(key => key, StringComparer.Ordinal))
        {
            if (_cohortGateFaults.TryRemove(memberPairKey, out var fault))
            {
                return fault;
            }
        }

        return null;
    }

    // Observes a completed background cohort gate without rethrowing: a fault is parked as typed data
    // for its member pair, and a clean completion clears that pair's transient-fault count.
    private void ObserveCohortGateCompletion(string memberPairKey, CohortGateRun run)
    {
        if (run.Completion.Task.Exception is { } aggregate)
        {
            var faults = memberPairKey.StartsWith("train:", StringComparison.Ordinal)
                ? _trainGateFaults
                : _cohortGateFaults;
            faults[memberPairKey] = new ConductorAcceptanceCohortGateFault(
                memberPairKey,
                run.MemberGoalIds,
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
            selection.Members
                .Select(member => member.GoalId.Value)
                .ToHashSet(StringComparer.Ordinal),
            ConductorAcceptanceCohortSelector.PairFingerprint(selection.Members[0], selection.Members[1]),
            fault);

    internal int RecordCohortGateTransientFault(string memberPairKey) =>
        _cohortGateFaultCounts.AddOrUpdate(memberPairKey, 1, (_, count) => count + 1);

    internal void ClearCohortGateTransientFaults(string memberPairKey) =>
        _cohortGateFaultCounts.TryRemove(memberPairKey, out _);

    private ConductorAcceptanceCohortRunResult CohortGateFaulted(
        IReadOnlyList<Goal> orderedGoals,
        ConductorAutonomyPolicy policy,
        ConductorAcceptanceCohortGateFault fault)
    {
        var goals = orderedGoals.Where(goal => fault.MemberGoalIds.Contains(goal.Id.Value)).ToArray();
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
