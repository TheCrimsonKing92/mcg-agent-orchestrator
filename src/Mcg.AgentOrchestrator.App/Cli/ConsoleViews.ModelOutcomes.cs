using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
    public static void PrintModelOutcomeScorecard(IReadOnlyList<ModelOutcomeRecord> records, BoundModelSet? boundModels)
    {
        Console.WriteLine();
        Console.WriteLine($"Model outcome scorecard ({records.Count} model(s)):");
        if (records.Count == 0)
        {
            Console.WriteLine("  No completed or failed dispatches with model selection found.");
            Console.WriteLine();
        }

        foreach (var record in records.Where(record => boundModels is not { IsAvailable: true } || record.IsBound))
        {
            Console.WriteLine();
            var lane = string.IsNullOrWhiteSpace(record.DispatchLane)
                ? "unrecorded"
                : record.DispatchLane;
            Console.WriteLine($"  {record.ProviderName}/{record.ModelName} lane={lane}: {record.Recommendation}");
            Console.WriteLine($"    completed={record.Completed} failed={record.Failed} " +
                $"findings={record.Findings} " +
                $"realFailed={record.RealFailures} environmentalFailed={record.EnvironmentalFailures} " +
                $"manufacturedFixedFailed={record.ManufacturedFixedFailures} unknownEraFailed={record.UnknownEraFailures} " +
                $"classMismatchFailed={record.ClassMismatchFailures} classMismatchRules={record.ClassMismatchRules} " +
                $"adequate={record.SelfRatedAdequate} overkill={record.SelfRatedOverkill} " +
                $"underpowered={record.SelfRatedUnderpowered} divergence={record.Divergence}");
            var mismatchReason = record.ClassMismatchFailures > 0
                ? $" Class-mismatch failures: {record.ClassMismatchFailures}."
                : string.Empty;
            Console.WriteLine($"    reason: {record.Reason}{mismatchReason}");
        }

        if (boundModels is not { IsAvailable: true })
        {
            Console.WriteLine($"  Bound model set unavailable ({boundModels?.UnavailableReason ?? "bound model set not supplied"}); all models listed as bound.");
        }
        else
        {
            var retired = records.Where(record => !record.IsBound).Select(record =>
                $"{record.ProviderName}/{record.ModelName} lane={(string.IsNullOrWhiteSpace(record.DispatchLane) ? "unrecorded" : record.DispatchLane)} " +
                $"completed={record.Completed} failed={record.Failed}").ToList();
            if (retired.Count > 0)
            {
                Console.WriteLine($"  Retired models: {string.Join("; ", retired)}");
            }
        }

        Console.WriteLine();
    }
}
