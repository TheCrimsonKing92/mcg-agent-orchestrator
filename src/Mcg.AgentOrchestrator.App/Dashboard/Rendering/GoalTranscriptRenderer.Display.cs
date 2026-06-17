using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Dashboard.Rendering;

public static partial class GoalTranscriptRenderer
{
    private static string Display(GoalStatus status) => DashboardDisplayNames.Display(status);
    private static string Display(WorkTaskStatus status) => DashboardDisplayNames.Display(status);
    private static string Display(ProgressKind kind) => DashboardDisplayNames.Display(kind);
    private static string Display(TaskAttentionKind kind) => DashboardDisplayNames.Display(kind);
    private static string Display(NextActionKind kind) => DashboardDisplayNames.Display(kind);
    private static string Display(VerificationGateStatus status) => DashboardDisplayNames.Display(status);
    private static string Display(GoalAcceptanceBlockerKind kind) => DashboardDisplayNames.Display(kind);
    private static string Display(TaskEvidenceKind kind) => DashboardDisplayNames.Display(kind);
    private static string Display(StageReadinessStatus status) => DashboardDisplayNames.Display(status);

    private static string FormatTokenUsage(int? inputTokens, int? outputTokens)
    {
        return $"{inputTokens?.ToString() ?? "n/a"} in / {outputTokens?.ToString() ?? "n/a"} out";
    }

    private static string FormatModelUsage(ModelUsageSummary usage)
    {
        var runs = usage.ExecutionCount == 1 ? "1 run" : $"{usage.ExecutionCount} runs";
        var complexity = usage.TaskComplexity is null ? string.Empty : $" ({usage.TaskComplexity.Value})";
        var paid = usage.IsPotentiallyPaidProvider ? " [potentially paid]" : string.Empty;
        var limitHits = usage.OutputTokenLimitHitCount > 0
            ? $", cap hits {usage.OutputTokenLimitHitCount}{FormatMaxOutputTokens(usage.MaxOutputTokens)}"
            : string.Empty;
        var prompt = usage.PromptCharacterCount is null
            ? string.Empty
            : $", prompt {usage.PromptCharacterCount.Value} chars";
        return $"{usage.ProviderName}/{usage.ModelName}{complexity}{paid}: {runs}, {FormatTokenUsage(usage.InputTokens, usage.OutputTokens)}{limitHits}{prompt}";
    }

    private static string FormatDispatchModelUsage(DispatchModelSummary dispatch)
    {
        var dispatches = dispatch.DispatchCount == 1 ? "1 dispatch" : $"{dispatch.DispatchCount} dispatches";
        var complexity = dispatch.TaskComplexity is null ? string.Empty : $" ({dispatch.TaskComplexity.Value})";
        var reasoning = string.IsNullOrWhiteSpace(dispatch.ReasoningEffort)
            ? string.Empty
            : $" reasoning {dispatch.ReasoningEffort}";
        var paid = dispatch.IsPotentiallyPaidProvider ? " [potentially paid]" : string.Empty;
        var prompt = dispatch.PromptCharacterCount is null
            ? string.Empty
            : $", prompt {dispatch.PromptCharacterCount.Value} chars";
        return $"{dispatch.ProviderName}/{dispatch.ModelName}{complexity}{reasoning}{paid}: {dispatches}{prompt}";
    }

    private static string FormatModelFitSummary(ModelFitSummary fit)
    {
        var notes = fit.NoteCount == 1 ? "1 note" : $"{fit.NoteCount} notes";
        var counts = BuildModelFitCounts(fit);
        var shapes = fit.TaskShapes is { Count: > 0 }
            ? $"; shapes {string.Join(", ", fit.TaskShapes)}"
            : string.Empty;
        return $"{fit.ProviderName}/{fit.ModelName}: {notes}; {string.Join(", ", counts)}{shapes}";
    }

    private static List<string> BuildModelFitCounts(ModelFitSummary fit)
    {
        var counts = new List<string>();
        AddModelFitCount(counts, "adequate", fit.AdequateCount);
        AddModelFitCount(counts, "overkill", fit.OverkillCount);
        AddModelFitCount(counts, "underpowered", fit.UnderpoweredCount);
        AddModelFitCount(counts, "unknown", fit.UnknownCount);
        return counts.Count == 0 ? ["none"] : counts;
    }

    private static void AddModelFitCount(List<string> counts, string label, int count)
    {
        if (count > 0)
        {
            counts.Add($"{label} {count}");
        }
    }

    private static string FormatMaxOutputTokens(int? maxOutputTokens)
    {
        return maxOutputTokens is null ? string.Empty : $" of {maxOutputTokens}";
    }
}
