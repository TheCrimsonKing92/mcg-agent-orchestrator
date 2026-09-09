using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
    public static void PrintLoopHealthReport(LoopHealthSnapshot snapshot)
    {
        var windowLabel = snapshot.GoalCount == 1 ? "1 goal" : $"{snapshot.GoalCount} goals";
        Console.WriteLine();
        Console.WriteLine($"Loop health report ({windowLabel}):");
        Console.WriteLine($"  Goals: {snapshot.GoalCount} total, {snapshot.CompletedGoalCount} completed");
        Console.WriteLine($"  Tasks: {snapshot.TotalTaskCount} total, {snapshot.TotalDispatchCount} dispatches");
        Console.WriteLine();
        Console.WriteLine($"  Dispatches / successful merge:  {FormatRate(snapshot.DispatchesPerSuccessfulMerge, "dispatches/merge", snapshot.CompletedGoalCount == 0 ? " (no completed goals)" : string.Empty)}");
        Console.WriteLine($"  False-completion catch rate:    {snapshot.FalseCompletionCatchRate:P0}");
        Console.WriteLine($"  Operator prompts / goal:        {snapshot.OperatorPromptsPerGoal:F2}");
        Console.WriteLine($"  Rework / retry rate:            {snapshot.ReworkRetryRate:P0}");
        Console.WriteLine($"  Median time to acceptance:      {FormatMedianHours(snapshot.MedianTimeToAcceptanceHours)}");
        Console.WriteLine($"  Paid retry dispatches / landed: {snapshot.PaidRetryDispatchesPerLandedGoal:F2} ({snapshot.PaidRetryDispatchCount} total)");
        Console.WriteLine($"  Same-context repeats prevented: {snapshot.SameFingerprintPreventedCount}");
        Console.WriteLine($"  Legacy same-context observed:   {snapshot.LegacyObservedSameFingerprintRepeatCount}");
        Console.WriteLine($"  Median retry resolution:        {FormatMedianHours(snapshot.MedianRetryResolutionHours)}");
        Console.WriteLine($"  Retry authority unavailable:    fingerprint={snapshot.RetryFingerprintUnavailableCount}, paid={snapshot.RetryPaidAuthorityUnknownCount}, cause={snapshot.RetryCauseUnavailableCount}");
        Console.WriteLine($"  Focused evidence:               attempts={snapshot.EvidenceAttemptCount}, elapsed_ms={snapshot.EvidenceElapsedMilliseconds?.ToString() ?? "unavailable"}, provider_usage_unavailable={snapshot.EvidenceProviderUsageUnavailableCount}");

        Console.WriteLine("  First-pass completion by role:");
        foreach (var role in snapshot.FirstPassCompletionByRole ?? [])
        {
            var rate = role.CompletionRate is null ? "N/A" : role.CompletionRate.Value.ToString("P0");
            Console.WriteLine($"    {role.Role}: {rate} ({role.FirstPassCompletedCount}/{role.PresentTaskCount})");
        }
        Console.WriteLine($"    All five roles: {snapshot.FiveRoleFirstPassGoalCount}/{snapshot.FiveRoleGoalCount} goals");

        Console.WriteLine("  Retry causes:");
        foreach (var cause in snapshot.RetryCauseDistribution ?? [])
            Console.WriteLine($"    {cause.Cause}: {cause.Count}");

        Console.WriteLine();
        Console.WriteLine("  Per-model outcome mix:");
        if (snapshot.ModelOutcomeMix.Count == 0)
        {
            Console.WriteLine("    No completed or failed dispatches with model selection found.");
        }
        else
        {
            foreach (var record in snapshot.ModelOutcomeMix)
            {
                Console.WriteLine($"    {record.ProviderName}/{record.ModelName}: {record.Recommendation}");
                Console.WriteLine($"      completed={record.Completed} failed={record.Failed} " +
                    $"adequate={record.SelfRatedAdequate} overkill={record.SelfRatedOverkill} " +
                    $"underpowered={record.SelfRatedUnderpowered}");
            }
        }

        Console.WriteLine();
    }

    private static string FormatRate(double value, string unit, string suffix = "")
    {
        return $"{value:F1} {unit}{suffix}";
    }

    private static string FormatMedianHours(double? hours)
    {
        if (hours is null)
        {
            return "n/a (no completed goals)";
        }

        if (hours.Value < 1.0)
        {
            return $"{hours.Value * 60.0:F0} min";
        }

        return $"{hours.Value:F1} hr";
    }
}
