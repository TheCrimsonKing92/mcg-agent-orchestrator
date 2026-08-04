namespace Mcg.AgentOrchestrator.Core;

public sealed record JudgeVerdictDistribution(
    string JudgeName,
    int MetCount,
    int NotMetCount,
    int NoVerdictCount);

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
    double FalsePassRate);

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
            falsePassRate);
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
}
