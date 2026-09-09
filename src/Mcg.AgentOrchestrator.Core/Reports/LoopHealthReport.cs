namespace Mcg.AgentOrchestrator.Core;

public sealed record JudgeVerdictDistribution(
    string JudgeName,
    int MetCount,
    int NotMetCount,
    int NoVerdictCount);

public sealed record RetryCauseDistribution(RetryCause Cause, int Count);

public sealed record RoleFirstPassCompletion(
    AgentRole Role,
    int PresentTaskCount,
    int FirstPassCompletedCount,
    double? CompletionRate);

public sealed record LoopHealthSnapshot(
    int GoalCount,
    int CompletedGoalCount,
    int TotalTaskCount,
    int TotalDispatchCount,
    double DispatchesPerSuccessfulMerge,
    double FalseCompletionCatchRate,
    double OperatorPromptsPerGoal,
    double ReworkRetryRate,
    double? MedianTimeToAcceptanceHours,
    IReadOnlyList<ModelOutcomeRecord> ModelOutcomeMix,
    IReadOnlyList<JudgeVerdictDistribution> JudgeVerdictDistributions,
    double InterJudgeAgreementRate,
    double FalseBlockRate,
    double FalsePassRate,
    int PaidRetryDispatchCount = 0,
    double PaidRetryDispatchesPerLandedGoal = 0,
    int SameFingerprintPreventedCount = 0,
    int LegacyObservedSameFingerprintRepeatCount = 0,
    IReadOnlyList<RetryCauseDistribution>? RetryCauseDistribution = null,
    double? MedianRetryResolutionHours = null,
    IReadOnlyList<RoleFirstPassCompletion>? FirstPassCompletionByRole = null,
    int FiveRoleFirstPassGoalCount = 0,
    int FiveRoleGoalCount = 0,
    int RetryFingerprintUnavailableCount = 0,
    int RetryPaidAuthorityUnknownCount = 0,
    int RetryCauseUnavailableCount = 0,
    int EvidenceAttemptCount = 0,
    long? EvidenceElapsedMilliseconds = null,
    int EvidenceProviderUsageUnavailableCount = 0);

public static class LoopHealthReport
{
    public static LoopHealthSnapshot Build(
        IEnumerable<Goal> goals,
        IEnumerable<HumanInputRequest> humanInputRequests,
        int? lastN = null,
        int modelOutcomeWindowSize = ModelOutcomeScorecard.DefaultWindowSize,
        IEnumerable<SemanticAcceptanceReceipt>? receipts = null)
    {
        var window = ApplyWindow(goals.ToList(), lastN);
        var goalIds = window.Select(g => g.Id).ToHashSet();
        var requests = humanInputRequests.Where(r => goalIds.Contains(r.GoalId)).ToList();

        var allTasks = window.SelectMany(g => g.Tasks).ToList();
        var completedGoals = window.Where(g => g.Status == GoalStatus.Completed).ToList();

        var totalDispatches = window.Sum(g =>
            g.Timeline.Count(e => e.Kind == ProgressKind.TaskDispatchRecorded));

        var dispatchesPerMerge = completedGoals.Count > 0
            ? (double)totalDispatches / completedGoals.Count
            : 0.0;

        var tasksWithAnyVerification = allTasks
            .Where(t => t.VerificationHistory.Count > 0)
            .ToList();
        var falseCompletionCount = tasksWithAnyVerification
            .Count(IsFalseCompletionCatch);
        var falseCompletionRate = tasksWithAnyVerification.Count > 0
            ? (double)falseCompletionCount / tasksWithAnyVerification.Count
            : 0.0;

        var promptsPerGoal = window.Count > 0
            ? (double)requests.Count / window.Count
            : 0.0;

        var retriedTaskIds = window
            .SelectMany(g => g.Timeline)
            .Where(e => e.Kind == ProgressKind.TaskRetried && e.TaskId is not null)
            .Select(e => e.TaskId!)
            .Distinct()
            .ToHashSet();
        var retryRate = allTasks.Count > 0
            ? (double)retriedTaskIds.Count / allTasks.Count
            : 0.0;

        double? medianHours = null;
        if (completedGoals.Count > 0)
        {
            var durations = completedGoals
                .Select(ComputeGoalDurationHours)
                .Where(h => h.HasValue)
                .Select(h => h!.Value)
                .OrderBy(h => h)
                .ToList();
            if (durations.Count > 0)
            {
                medianHours = ComputeMedian(durations);
            }
        }

        var modelMix = ModelOutcomeScorecard.Build(window, modelOutcomeWindowSize);

        var receiptList = receipts?.ToList() ?? [];
        var goalStatusById = window.ToDictionary(g => g.Id.Value, g => g.Status, StringComparer.Ordinal);

        var judgeDistributions = BuildJudgeVerdictDistributions(receiptList);
        var agreementRate = ComputeInterJudgeAgreementRate(receiptList);
        var (falseBlockRate, falsePassRate) = ComputeFalseBlockPassRates(receiptList, goalStatusById);
        var admissions = allTasks.SelectMany(task => task.RetryAdmissionHistory).ToList();
        var landedAdmissions = completedGoals
            .SelectMany(goal => goal.Tasks.SelectMany(task =>
                task.RetryAdmissionHistory.Select(receipt => (TaskId: task.Id, Receipt: receipt))))
            .ToList();
        var paidRetryDispatchCount = landedAdmissions
            .Where(attempt =>
                attempt.Receipt.PaidRoute == PaidRouteClassification.Paid &&
                attempt.Receipt.Decision is RetryAdmissionDecision.Allowed or RetryAdmissionDecision.ResumedReservation &&
                attempt.Receipt.PriorAttemptAt is not null &&
                attempt.Receipt.WorkerStartedAt is not null)
            .Select(attempt => (attempt.TaskId, attempt.Receipt.LinkedDispatchAt))
            .Distinct()
            .Count();
        var paidRetriesPerLandedGoal = completedGoals.Count == 0
            ? 0.0
            : (double)paidRetryDispatchCount / completedGoals.Count;
        var preventedSameFingerprint = admissions.Count(receipt =>
            receipt.Decision == RetryAdmissionDecision.Prevented);
        var legacyObservedSameFingerprint = allTasks.Sum(CountLegacyObservedSameFingerprintRepeats);
        var legacyCauseUnavailable = allTasks.Sum(CountLegacyRetryAttemptsWithoutAdmission);
        var causeDistribution = Enum.GetValues<RetryCause>()
            .Select(cause => new RetryCauseDistribution(
                cause,
                admissions.Count(receipt =>
                    receipt.PaidRoute == PaidRouteClassification.Paid &&
                    receipt.PriorAttemptAt is not null &&
                    receipt.Cause == cause) +
                (cause == RetryCause.Unknown ? legacyCauseUnavailable : 0)))
            .ToArray();
        var retryResolutionDurations = window
            .SelectMany(goal => goal.Tasks.Select(task => ComputeRetryResolutionHours(goal, task)))
            .Where(duration => duration.HasValue)
            .Select(duration => duration!.Value)
            .OrderBy(duration => duration)
            .ToList();
        var firstPassByRole = BuildFirstPassCompletion(window);
        var fiveRoleFirstPassGoals = CountFiveRoleFirstPassGoals(window);
        var fiveRoleGoals = CountFiveRoleGoals(window);
        var fingerprintUnavailable = allTasks.Sum(task => task.DispatchHistory
            .Skip(1)
            .Count(dispatch => dispatch.RetryContextFingerprint is null));
        var paidAuthorityUnknown = allTasks.Sum(task => task.DispatchHistory
            .Skip(1)
            .Count(dispatch => dispatch.PaidRoute == PaidRouteClassification.Unknown));
        var evidenceAttemptCount = allTasks.Sum(task => task.PreReviewEvidenceAttemptCount);
        // Focused receipts do not carry provider usage or elapsed time. Null is deliberately
        // distinguishable from a measured zero until the attempt artifact contract supplies it.
        long? evidenceElapsedMilliseconds = null;

        return new LoopHealthSnapshot(
            window.Count,
            completedGoals.Count,
            allTasks.Count,
            totalDispatches,
            dispatchesPerMerge,
            falseCompletionRate,
            promptsPerGoal,
            retryRate,
            medianHours,
            modelMix,
            judgeDistributions,
            agreementRate,
            falseBlockRate,
            falsePassRate,
            paidRetryDispatchCount,
            paidRetriesPerLandedGoal,
            preventedSameFingerprint,
            legacyObservedSameFingerprint,
            causeDistribution,
            retryResolutionDurations.Count == 0 ? null : ComputeMedian(retryResolutionDurations),
            firstPassByRole,
            fiveRoleFirstPassGoals,
            fiveRoleGoals,
            fingerprintUnavailable,
            paidAuthorityUnknown,
            legacyCauseUnavailable,
            evidenceAttemptCount,
            evidenceElapsedMilliseconds,
            evidenceAttemptCount);
    }

    // Emitted by BackgroundDispatchRunner when a dispatch exits 0 but the file-change guard fires.
    private const string FalsePositiveRejectionMarker =
        "did not produce required relevant file-change evidence";

    // A false completion is caught when any verification record's StandardError contains the
    // rejection marker from the no-relevant-file-change completion guard.
    private static bool IsFalseCompletionCatch(TaskSpec task) =>
        task.VerificationHistory.Any(v =>
            v.StandardError.Contains(FalsePositiveRejectionMarker, StringComparison.Ordinal));

    private static IReadOnlyList<JudgeVerdictDistribution> BuildJudgeVerdictDistributions(
        IReadOnlyList<SemanticAcceptanceReceipt> receipts)
    {
        var byJudge = new Dictionary<string, (int Met, int NotMet, int NoVerdict)>(StringComparer.Ordinal);
        foreach (var receipt in receipts)
        {
            foreach (var entry in receipt.Judges)
            {
                byJudge.TryGetValue(entry.Judge, out var counts);
                if (!entry.Valid)
                    byJudge[entry.Judge] = counts with { NoVerdict = counts.NoVerdict + 1 };
                else if (entry.CriteriaMet)
                    byJudge[entry.Judge] = counts with { Met = counts.Met + 1 };
                else
                    byJudge[entry.Judge] = counts with { NotMet = counts.NotMet + 1 };
            }
        }

        return byJudge
            .Select(kv => new JudgeVerdictDistribution(kv.Key, kv.Value.Met, kv.Value.NotMet, kv.Value.NoVerdict))
            .OrderBy(d => d.JudgeName, StringComparer.Ordinal)
            .ToList();
    }

    private static double ComputeInterJudgeAgreementRate(IReadOnlyList<SemanticAcceptanceReceipt> receipts)
    {
        var totalWithMultiple = 0;
        var agreeing = 0;
        foreach (var receipt in receipts)
        {
            var validJudges = receipt.Judges.Where(j => j.Valid).ToList();
            if (validJudges.Count < 2) continue;
            totalWithMultiple++;
            if (validJudges.All(j => j.CriteriaMet == validJudges[0].CriteriaMet))
                agreeing++;
        }

        return totalWithMultiple > 0 ? (double)agreeing / totalWithMultiple : 0.0;
    }

    // Denominates over receipts where consensus is non-null AND goal has a terminal status.
    // FalseBlock: consensus=false (NOT-MET) but goal Completed (would have wrongly blocked a landed goal).
    // FalsePass:  consensus=true  (MET)     but goal Failed/Cancelled/Superseded (passed a failed goal).
    private static (double FalseBlockRate, double FalsePassRate) ComputeFalseBlockPassRates(
        IReadOnlyList<SemanticAcceptanceReceipt> receipts,
        Dictionary<string, GoalStatus> goalStatusById)
    {
        var denominator = 0;
        var falseBlocks = 0;
        var falsePasses = 0;

        foreach (var receipt in receipts)
        {
            if (receipt.Consensus is null) continue;
            if (!goalStatusById.TryGetValue(receipt.GoalId, out var status)) continue;

            var isTerminal = status is GoalStatus.Completed or GoalStatus.Failed
                or GoalStatus.Cancelled or GoalStatus.Superseded;
            if (!isTerminal) continue;

            denominator++;
            if (!receipt.Consensus.Value && status == GoalStatus.Completed)
                falseBlocks++;
            else if (receipt.Consensus.Value && status is GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded)
                falsePasses++;
        }

        if (denominator == 0) return (0.0, 0.0);
        return ((double)falseBlocks / denominator, (double)falsePasses / denominator);
    }

    private static List<Goal> ApplyWindow(List<Goal> goals, int? lastN)
    {
        if (lastN is null or <= 0)
        {
            return goals;
        }

        return goals
            .OrderBy(g => g.Timeline
                .FirstOrDefault(e => e.Kind == ProgressKind.GoalCreated)?.OccurredAt
                ?? DateTimeOffset.MinValue)
            .TakeLast(lastN.Value)
            .ToList();
    }

    private static double? ComputeGoalDurationHours(Goal goal)
    {
        var events = goal.Timeline.OrderBy(e => e.OccurredAt).ToList();
        if (events.Count < 2)
        {
            return null;
        }

        return (events[^1].OccurredAt - events[0].OccurredAt).TotalHours;
    }

    private static double ComputeMedian(List<double> sorted)
    {
        var n = sorted.Count;
        return n % 2 == 1
            ? sorted[n / 2]
            : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
    }

    private static int CountLegacyObservedSameFingerprintRepeats(TaskSpec task)
    {
        var receiptAttempts = task.RetryAdmissionHistory
            .Select(receipt => receipt.LinkedDispatchAt)
            .ToHashSet();
        return task.DispatchHistory
            .Where(dispatch =>
                dispatch.PaidRoute == PaidRouteClassification.Paid &&
                dispatch.RetryContextFingerprint is not null &&
                !receiptAttempts.Contains(dispatch.DispatchedAt))
            .GroupBy(dispatch => dispatch.RetryContextFingerprint!.Value, StringComparer.Ordinal)
            .Sum(group => Math.Max(0, group.Count() - 1));
    }

    private static int CountLegacyRetryAttemptsWithoutAdmission(TaskSpec task)
    {
        var receiptAttempts = task.RetryAdmissionHistory
            .Select(receipt => receipt.LinkedDispatchAt)
            .ToHashSet();
        return task.DispatchHistory
            .Skip(1)
            .Count(dispatch =>
                dispatch.PaidRoute == PaidRouteClassification.Paid &&
                !receiptAttempts.Contains(dispatch.DispatchedAt));
    }

    private static double? ComputeRetryResolutionHours(Goal goal, TaskSpec task)
    {
        var retryReceipts = task.RetryAdmissionHistory
            .Where(receipt =>
                receipt.PaidRoute == PaidRouteClassification.Paid &&
                receipt.PriorAttemptAt is not null)
            .OrderBy(receipt => receipt.RecordedAt)
            .ToArray();
        if (retryReceipts.Length == 0)
            return null;

        var priorAttemptAt = retryReceipts[0].PriorAttemptAt!.Value;
        var resolutionEligibleAt = retryReceipts[0].RecordedAt;
        var unsuccessfulVerificationAt = task.VerificationHistory
            .Where(verification =>
                !verification.Succeeded &&
                verification.CompletedAt >= priorAttemptAt &&
                verification.CompletedAt <= resolutionEligibleAt)
            .OrderBy(verification => verification.CompletedAt)
            .FirstOrDefault()?.CompletedAt;
        var unsuccessfulTaskAt = goal.Timeline
            .Where(evt =>
                evt.TaskId == task.Id &&
                evt.OccurredAt >= priorAttemptAt &&
                evt.OccurredAt <= resolutionEligibleAt &&
                evt.Kind is ProgressKind.TaskFailed or ProgressKind.TaskCancelled)
            .OrderBy(evt => evt.OccurredAt)
            .FirstOrDefault()?.OccurredAt;
        var start = new[] { unsuccessfulVerificationAt, unsuccessfulTaskAt }
            .Where(candidate => candidate is not null)
            .Min() ?? resolutionEligibleAt;
        var successfulVerification = task.VerificationHistory
            .Where(verification => verification.Succeeded && verification.CompletedAt >= resolutionEligibleAt)
            .OrderBy(verification => verification.CompletedAt)
            .FirstOrDefault()?.CompletedAt;
        var taskTimeline = goal.Timeline
            .Where(evt => evt.TaskId == task.Id)
            .ToArray();
        var terminalTaskAt = taskTimeline
            .Select((evt, index) => (Event: evt, Index: index))
            .Where(candidate =>
                candidate.Event.OccurredAt >= resolutionEligibleAt &&
                candidate.Event.Kind is ProgressKind.TaskCompleted or ProgressKind.TaskFailed or ProgressKind.TaskCancelled &&
                !taskTimeline.Skip(candidate.Index + 1).Any(later => later.Kind == ProgressKind.TaskRetried))
            .OrderBy(candidate => candidate.Event.OccurredAt)
            .FirstOrDefault().Event?.OccurredAt;
        var terminalGoalAt = goal.Timeline
            .Where(evt =>
                evt.OccurredAt >= resolutionEligibleAt &&
                evt.Kind is ProgressKind.GoalCancelled or ProgressKind.GoalSuperseded)
            .OrderBy(evt => evt.OccurredAt)
            .FirstOrDefault()?.OccurredAt;
        var terminalResolution = new[] { terminalTaskAt, terminalGoalAt }
            .Where(candidate => candidate is not null)
            .Min();
        var end = new[] { successfulVerification, terminalResolution }
            .Where(candidate => candidate is not null)
            .Min();
        return end is not null && end >= start
            ? (end.Value - start).TotalHours
            : null;
    }

    private static IReadOnlyList<RoleFirstPassCompletion> BuildFirstPassCompletion(IReadOnlyList<Goal> goals)
    {
        var roles = new[]
        {
            AgentRole.Researcher,
            AgentRole.Planner,
            AgentRole.Developer,
            AgentRole.Tester,
            AgentRole.Reviewer
        };
        return roles.Select(role =>
        {
            var tasks = goals.SelectMany(goal => goal.Tasks).Where(task => task.RequiredRole == role).ToArray();
            var completed = tasks.Count(task => IsFirstPassCompleted(goals, task));
            return new RoleFirstPassCompletion(
                role,
                tasks.Length,
                completed,
                tasks.Length == 0 ? null : (double)completed / tasks.Length);
        }).ToArray();
    }

    private static int CountFiveRoleFirstPassGoals(IReadOnlyList<Goal> goals)
    {
        var roles = new HashSet<AgentRole>
        {
            AgentRole.Researcher,
            AgentRole.Planner,
            AgentRole.Developer,
            AgentRole.Tester,
            AgentRole.Reviewer
        };
        return goals.Count(goal =>
            roles.All(role => goal.Tasks.Any(task => task.RequiredRole == role)) &&
            goal.Tasks.Where(task => roles.Contains(task.RequiredRole)).All(task => IsFirstPassCompleted([goal], task)));
    }

    private static int CountFiveRoleGoals(IReadOnlyList<Goal> goals)
    {
        var roles = new HashSet<AgentRole>
        {
            AgentRole.Researcher,
            AgentRole.Planner,
            AgentRole.Developer,
            AgentRole.Tester,
            AgentRole.Reviewer
        };
        return goals.Count(goal => roles.All(role => goal.Tasks.Any(task => task.RequiredRole == role)));
    }

    private static bool IsFirstPassCompleted(IEnumerable<Goal> goals, TaskSpec task)
    {
        var goal = goals.Single(candidate => candidate.Tasks.Contains(task));
        return task.Status == WorkTaskStatus.Completed &&
            task.DispatchHistory.Count <= 1 &&
            !goal.Timeline.Any(evt => evt.TaskId == task.Id && evt.Kind == ProgressKind.TaskRetried);
    }
}
