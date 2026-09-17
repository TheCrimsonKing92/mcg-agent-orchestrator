using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    internal sealed record LiveAcceptanceCensus(
        IReadOnlyList<string> Occupants,
        Exception? CaptureFailure = null)
    {
        internal int OccupiedCount => Occupants.Count;

        internal string Describe() => Occupants.Count == 0
            ? "live acceptance occupants none"
            : $"live acceptance occupants {string.Join(',', Occupants)}";
    }

    internal sealed record LiveAcceptanceAdmissionDecision(bool IsAdmitted, string Reason);

    private sealed record ActiveParallelAcceptanceReservations(
        IReadOnlyList<ConductorParallelAcceptanceAttempt> Attempts,
        List<ConductorParallelAcceptanceCandidate> Candidates,
        HashSet<string> AttemptIds,
        HashSet<int> StableSlotIndexes,
        Exception? Failure = null);

    private static ActiveParallelAcceptanceReservations BuildActiveParallelAcceptanceReservations(
        ConductorParallelAcceptanceAttemptCoordinator coordinator,
        IReadOnlyCollection<Goal> goals)
    {
        try
        {
            var liveAttempts = coordinator.GetCapacityReservingAttempts(goals.Select(goal => goal.Id.Value));
            var goalsById = goals.ToDictionary(goal => goal.Id.Value, StringComparer.Ordinal);
            var candidates = liveAttempts
                .Where(attempt => goalsById.ContainsKey(attempt.GoalId))
                .Select(attempt => ConductorParallelAcceptanceCandidate.Create(
                    goalsById[attempt.GoalId],
                    attempt.SlotIndex,
                    attempt.ScopePaths ?? [],
                    attempt.BranchHeadSha,
                    attempt.MainHeadSha))
                .ToList();
            return new ActiveParallelAcceptanceReservations(
                liveAttempts,
                candidates,
                liveAttempts.Select(attempt => attempt.AttemptId).ToHashSet(StringComparer.Ordinal),
                liveAttempts.Select(attempt => attempt.SlotIndex).ToHashSet());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new ActiveParallelAcceptanceReservations([], [], [], [], ex);
        }
    }

    internal static LiveAcceptanceCensus BuildLiveAcceptanceCensus(
        IReadOnlyList<ConductorParallelAcceptanceAttempt> activeAttempts,
        IReadOnlySet<string> activeAttemptIds,
        ConductorAcceptanceCapacitySnapshot activeCohorts,
        IReadOnlyList<GateLoadContextProbe.LiveGateOccupant> liveGates)
    {
        var occupants = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var claimedProcessIds = new HashSet<int>();
        var claimedGoalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var attemptId in activeAttemptIds.OrderBy(id => id, StringComparer.Ordinal))
        {
            var attempt = activeAttempts.FirstOrDefault(candidate =>
                string.Equals(candidate.AttemptId, attemptId, StringComparison.Ordinal));
            if (attempt is null)
            {
                occupants.TryAdd($"attempt:{attemptId}", $"attempt:{ShortIdentity(attemptId)}");
                continue;
            }

            var key = attempt.OwnerProcessId > 0
                ? $"pid:{attempt.OwnerProcessId}"
                : $"attempt:{attempt.AttemptId}";
            occupants.TryAdd(key, $"goal:{ShortIdentity(attempt.GoalId)}");
            if (attempt.OwnerProcessId > 0)
            {
                claimedProcessIds.Add(attempt.OwnerProcessId);
            }
            claimedGoalIds.Add(attempt.GoalId);
        }

        foreach (var root in activeCohorts.ActiveRoots.OrderBy(root => root.Key, StringComparer.Ordinal))
        {
            var memberIds = root.MemberGoalIds.OrderBy(id => id, StringComparer.Ordinal).ToArray();
            occupants.TryAdd(
                $"cohort:{root.Key}",
                memberIds.Length == 0 ? $"root:{ShortIdentity(root.Key)}" : $"goal:{ShortIdentity(memberIds[0])}");
            claimedGoalIds.UnionWith(memberIds);
        }

        foreach (var gate in liveGates.OrderBy(gate => gate.Identity, StringComparer.OrdinalIgnoreCase))
        {
            if ((gate.ProcessId is > 0 && claimedProcessIds.Contains(gate.ProcessId.Value)) ||
                (!string.IsNullOrWhiteSpace(gate.GoalId) && claimedGoalIds.Contains(gate.GoalId)))
            {
                continue;
            }

            occupants.TryAdd(
                gate.Identity,
                !string.IsNullOrWhiteSpace(gate.GoalId)
                    ? $"goal:{ShortIdentity(gate.GoalId)}"
                    : gate.ProcessId is > 0
                        ? $"pid:{gate.ProcessId.Value}"
                        : $"slot:{gate.SlotIndex}");
        }

        return new LiveAcceptanceCensus(
            occupants.Values.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    internal static LiveAcceptanceAdmissionDecision DecideLiveAcceptanceAdmission(
        LiveAcceptanceCensus census,
        int width)
    {
        if (census.CaptureFailure is { } captureFailure)
        {
            return new LiveAcceptanceAdmissionDecision(
                false,
                $"live acceptance census unavailable: {SanitizeReason(captureFailure.Message)}; retry on next conduct tick");
        }

        return census.OccupiedCount < width
            ? new LiveAcceptanceAdmissionDecision(true, string.Empty)
            : new LiveAcceptanceAdmissionDecision(
                false,
                $"acceptance width {width} reached; {census.Describe()}; retry on next conduct tick");
    }

    private static LiveAcceptanceCensus CaptureLiveAcceptanceCensus(
        IReadOnlyList<ConductorParallelAcceptanceAttempt> activeAttempts,
        IReadOnlySet<string> activeAttemptIds,
        ConductorAcceptanceCapacitySnapshot activeCohorts,
        int tick,
        List<string> changedGoalLines,
        bool blockAdmissionOnFailure)
    {
        try
        {
            return BuildLiveAcceptanceCensus(
                activeAttempts,
                activeAttemptIds,
                activeCohorts,
                GateLoadContextProbe.CaptureLiveGateOccupants());
        }
        catch (Exception ex) when (ex is GateLoadContextProbe.LoadProbeUnavailableException or IOException or UnauthorizedAccessException)
        {
            RecordParallelAcceptanceProgress(
                $"ADMISSION tick={tick} detail=live-census-unavailable error={SanitizeReason(ex.Message)}",
                changedGoalLines);
            var lifecycleCensus = BuildLiveAcceptanceCensus(activeAttempts, activeAttemptIds, activeCohorts, []);
            return blockAdmissionOnFailure
                ? lifecycleCensus with { CaptureFailure = ex }
                : lifecycleCensus;
        }
    }

    private static string ShortIdentity(string value) =>
        value.Length <= 8 ? value : value[..8];

    private static ParallelLandingOutcome ReserveRunningParallelAcceptanceAttempts(
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        Goal goal,
        ConductorAutonomyPolicy policy,
        int tick,
        IReadOnlyList<ConductorParallelAcceptanceAttemptDecision> running,
        List<string> changedGoalLines,
        HashSet<GoalId> changedGoalIds)
    {
        var runningCandidates = running
            .Select(runningDecision => ConductorParallelAcceptanceCandidate.Create(
                goal,
                runningDecision.Attempt.SlotIndex,
                runningDecision.Attempt.ScopePaths ?? [],
                runningDecision.Attempt.BranchHeadSha,
                runningDecision.Attempt.MainHeadSha))
            .ToArray();
        foreach (var runningDecision in running)
        {
            ReplayParallelAcceptanceLeaseReceipts(driver, runningDecision.Attempt, changedGoalLines);
        }

        var retainedRunning = running[0];
        var retainedCandidate = runningCandidates[0];
        if (MarkParallelAcceptanceStarted(kernel, goal, retainedRunning.Attempt, tick))
        {
            changedGoalIds.Add(goal.Id);
        }

        RecordParallelAcceptanceProgress(
            AcceptanceLifecycleEventFormatter.Format(
                retainedCandidate.GoalPrefix,
                retainedCandidate.SlotIndex,
                "running",
                retainedRunning.Attempt.AttemptId,
                tick),
            changedGoalLines);
        return new ParallelLandingOutcome(
            ParallelAcceptanceHeld(
                retainedCandidate,
                policy,
                "acceptance verification still running in background"),
            retainedCandidate.SlotIndex);
    }

    private static int SelectAvailableParallelAcceptanceSlot(
        IReadOnlySet<int> activeAttemptSlotIndexes,
        int acceptanceSlotCount)
    {
        for (var slot = 0; slot < acceptanceSlotCount; slot++)
        {
            if (!activeAttemptSlotIndexes.Contains(slot))
            {
                return slot;
            }
        }

        throw new InvalidOperationException("Acceptance capacity reported room but every stable slot is reserved.");
    }

    private static void ReserveParallelAcceptanceCandidate(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorParallelAcceptanceAttempt attempt,
        List<ConductorParallelAcceptanceAttempt> activeAttempts,
        List<ConductorParallelAcceptanceCandidate> activeCandidates,
        HashSet<string> activeAttemptIds,
        HashSet<int> activeAttemptSlotIndexes)
    {
        if (!activeAttemptIds.Add(attempt.AttemptId))
        {
            return;
        }

        activeAttempts.Add(attempt);
        activeCandidates.Add(candidate);
        activeAttemptSlotIndexes.Add(candidate.SlotIndex);
    }

    private static string FormatCohortPairExclusions(
        IReadOnlyList<ConductorAcceptanceCohortPairExclusion> exclusions) =>
        string.Join(',', exclusions.Select(exclusion =>
            $"{exclusion.FirstGoalId.Value[..8]}:{exclusion.SecondGoalId.Value[..8]}:{exclusion.Reason}"));

    private static ConductorParallelAcceptanceCandidate? TryBuildParallelAcceptanceCandidate(
        ConductorDriver driver,
        Goal goal,
        ConductorAutonomyPolicy policy,
        int slotIndex,
        out Exception? exception)
    {
        exception = null;
        try
        {
            return driver.TryBuildParallelAcceptanceCandidate(goal, policy, slotIndex);
        }
        catch (Exception ex)
        {
            exception = ex;
            return null;
        }
    }

    private static string FormatParallelAcceptanceCandidateUnavailable(Exception exception) =>
        exception is ConductorDriver.EvidenceMutationLeaseUnavailableException
            ? exception.Message
            : $"parallel acceptance candidate unavailable; retry on next conduct tick: {SanitizeReason(exception.Message)}";

    private static string FormatParallelAcceptanceCandidateUnavailableDetail(Exception exception) =>
        SanitizeReason(exception.Message);

    private static IReadOnlyList<Goal> OrderParallelAcceptanceEligibleGoals(IReadOnlyList<Goal> eligible) =>
        eligible
            .OrderBy(ParallelAcceptanceVerifiedAt)
            .ThenBy(goal => goal.Timeline.FirstOrDefault()?.OccurredAt ?? DateTimeOffset.MinValue)
            .ThenBy(goal => goal.Id.Value, StringComparer.Ordinal)
            .ToArray();

    private static DateTimeOffset ParallelAcceptanceVerifiedAt(Goal goal)
    {
        var lastVerification = goal.Tasks
            .Select(task => task.LastVerification?.CompletedAt)
            .Where(completedAt => completedAt.HasValue)
            .Select(completedAt => completedAt!.Value)
            .DefaultIfEmpty(goal.Timeline.FirstOrDefault()?.OccurredAt ?? DateTimeOffset.MinValue)
            .Max();
        return goal.Timeline
            .Where(evt =>
                evt.Kind == ProgressKind.GoalPolicyDecision &&
                evt.Message.Contains("Verified", StringComparison.OrdinalIgnoreCase))
            .Select(evt => evt.OccurredAt)
            .DefaultIfEmpty(lastVerification)
            .Min();
    }
}
