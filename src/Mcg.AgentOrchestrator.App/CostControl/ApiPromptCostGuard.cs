using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.CostControl;

internal sealed record PaidApiPromptRisk(
    string ProviderName,
    string ModelName,
    TaskComplexity TaskComplexity,
    int PromptCharacterCount,
    int PromptThreshold,
    bool PromptExceedsThreshold,
    bool UsesComplexPaidModel,
    bool HasPriorOverkillFit = false,
    bool HasPriorUnderpoweredFit = false,
    int PriorOverkillCount = 0,
    int PriorUnderpoweredCount = 0,
    IReadOnlyList<string>? PriorTaskShapes = null);

internal static class ApiPromptCostGuard
{
    public const string DashboardConfirmationQueryName = "confirmLargePaidApiPrompt";
    public const string CliConfirmationFlag = "--confirm-large-paid-api-prompt";
    private const int SimplePaidApiPrompt = 4000;
    private const int ComplexPaidApiPrompt = 6000;

    public static PaidApiPromptRisk? Evaluate(AgentTaskRunPreview preview)
    {
        return Evaluate(preview, null);
    }

    public static PaidApiPromptRisk? Evaluate(AgentTaskRunPreview preview, Goal? goal)
    {
        if (!ProviderSmokeRunner.IsPaidProviderName(preview.ProviderName))
        {
            return null;
        }

        var fit = goal is null ? null : FindModelFit(goal, preview.ProviderName, preview.ModelName);
        var usesComplexPaidModel = preview.TaskComplexity == TaskComplexity.Complex || preview.UsesComplexModel;
        var threshold = usesComplexPaidModel ? ComplexPaidApiPrompt : SimplePaidApiPrompt;
        var promptExceedsThreshold = preview.PromptCharacterCount > threshold;
        var hasPriorOverkillFit = fit?.OverkillCount > 0;
        var hasPriorUnderpoweredFit = fit?.UnderpoweredCount > 0;
        return !promptExceedsThreshold && !usesComplexPaidModel && !hasPriorOverkillFit && !hasPriorUnderpoweredFit
            ? null
            : new PaidApiPromptRisk(
                preview.ProviderName,
                preview.ModelName,
                preview.TaskComplexity,
                preview.PromptCharacterCount,
                threshold,
                promptExceedsThreshold,
                usesComplexPaidModel,
                hasPriorOverkillFit,
                hasPriorUnderpoweredFit,
                fit?.OverkillCount ?? 0,
                fit?.UnderpoweredCount ?? 0,
                fit?.TaskShapes ?? []);
    }

    public static void ThrowIfConfirmationRequired(PaidApiPromptRisk? risk, bool confirmed)
    {
        if (risk is null || confirmed)
        {
            return;
        }

        throw new InvalidOperationException(BuildCliMessage(risk));
    }

    public static string BuildDashboardMessage(PaidApiPromptRisk risk)
    {
        return BuildMessage(
            risk,
            $"add {DashboardConfirmationQueryName}=true after inspecting the API plan");
    }

    private static string BuildCliMessage(PaidApiPromptRisk risk)
    {
        return BuildMessage(
            risk,
            $"rerun with {CliConfirmationFlag} after inspecting the API plan");
    }

    public static string BuildInlineLabel(PaidApiPromptRisk risk)
    {
        if (risk.PromptExceedsThreshold)
        {
            return $"large paid prompt: exceeds {risk.PromptThreshold}";
        }

        if (risk.HasPriorUnderpoweredFit)
        {
            return "prior underpowered API model";
        }

        if (risk.HasPriorOverkillFit)
        {
            return "prior overkill API model";
        }

        return "complex paid API model";
    }

    public static string? BuildRecommendation(PaidApiPromptRisk risk)
    {
        if (risk.HasPriorUnderpoweredFit)
        {
            return $"Prior evidence says {risk.ProviderName}/{risk.ModelName} was underpowered; choose a stronger model before paid API run.";
        }

        if (risk.HasPriorOverkillFit)
        {
            return CostRecommendationText.PaidApiOverkill(risk.ProviderName, risk.ModelName);
        }

        if (risk.PromptExceedsThreshold)
        {
            return "Inspect the generated prompt before paid API run; it exceeds the routine prompt threshold.";
        }

        if (risk.UsesComplexPaidModel)
        {
            return "Confirm this task needs the complex paid model before API run.";
        }

        return null;
    }

    private static string BuildMessage(PaidApiPromptRisk risk, string confirmationInstruction)
    {
        var reasons = new List<string>();
        if (risk.PromptExceedsThreshold)
        {
            reasons.Add($"prompt {risk.PromptCharacterCount} chars exceeds {risk.PromptThreshold}");
        }

        if (risk.UsesComplexPaidModel)
        {
            reasons.Add("uses complex paid model selection");
        }

        if (risk.HasPriorOverkillFit)
        {
            reasons.Add($"{risk.PriorOverkillCount} prior overkill model-fit note(s){FormatPriorTaskShapes(risk.PriorTaskShapes)}; {CostRecommendationText.LocalModelSwitchAction}");
        }

        if (risk.HasPriorUnderpoweredFit)
        {
            reasons.Add($"{risk.PriorUnderpoweredCount} prior underpowered model-fit note(s){FormatPriorTaskShapes(risk.PriorTaskShapes)}; consider a stronger model");
        }

        var reason = string.Join("; ", reasons);
        var recommendation = BuildRecommendation(risk);
        var recommendationText = string.IsNullOrWhiteSpace(recommendation)
            ? string.Empty
            : $" {recommendation}";
        return $"Paid API run requires explicit confirmation: {risk.ProviderName}/{risk.ModelName} {risk.TaskComplexity} {reason}.{recommendationText} {confirmationInstruction}.";
    }

    private static string FormatPriorTaskShapes(IReadOnlyList<string>? taskShapes)
    {
        return taskShapes is { Count: > 0 }
            ? $" on shapes {string.Join(", ", taskShapes)}"
            : string.Empty;
    }

    private static ModelFitSummary? FindModelFit(Goal goal, string providerName, string modelName)
    {
        return ModelFitEvidence
            .BuildSummary(goal.Tasks.SelectMany(ModelFitEvidence.FindNotes))
            .FirstOrDefault(fit =>
                fit.ProviderName.Equals(providerName, StringComparison.OrdinalIgnoreCase) &&
                fit.ModelName.Equals(modelName, StringComparison.OrdinalIgnoreCase));
    }
}
