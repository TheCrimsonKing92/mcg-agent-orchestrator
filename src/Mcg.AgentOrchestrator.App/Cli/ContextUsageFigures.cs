using System.Globalization;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal readonly record struct ContextUsageFigure(long? Value, string? UnknownReason = null)
{
    public string Display => Value?.ToString(CultureInfo.InvariantCulture) ??
        $"unknown({UnknownReason ?? "not recorded"})";
}

internal static class ContextUsageFigures
{
    internal static ContextUsageFigure Provider(ProviderUsageValue value) =>
        value.State == ProviderUsageState.Reported
            ? new(value.Value)
            : new(null, value.UnknownReason);

    internal static ContextUsageFigure Uncached(WorkerContextPackageReceipt receipt)
    {
        var input = Provider(receipt.InputTokens);
        var cached = Provider(receipt.CachedInputTokens);
        return input.Value is { } inputValue && cached.Value is { } cachedValue
            ? new(inputValue - cachedValue)
            : new(null, input.UnknownReason ?? cached.UnknownReason);
    }

    internal static ContextUsageFigure HarnessOverhead(WorkerContextPackageReceipt receipt)
    {
        var input = Provider(receipt.InputTokens);
        if (input.Value is null)
            return input;
        return receipt.ModelInputTokenEstimate > 0
            ? new(input.Value.Value - receipt.ModelInputTokenEstimate)
            : new(null, "no model input estimate");
    }

    internal static ContextUsageFigure Estimate(WorkerContextPackageReceipt receipt) =>
        receipt.ModelInputTokenEstimate > 0
            ? new(receipt.ModelInputTokenEstimate)
            : new(null, "no model input estimate");

    internal static ContextUsageFigure PromptBytes(WorkerContextPackageReceipt receipt) =>
        receipt.RenderedPromptBytes > 0
            ? new(receipt.RenderedPromptBytes)
            : new(null, "not recorded");
}
