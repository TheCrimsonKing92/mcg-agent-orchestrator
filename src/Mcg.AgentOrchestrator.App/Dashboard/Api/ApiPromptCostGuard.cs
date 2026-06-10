using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal sealed record PaidApiPromptRisk(
    string ProviderName,
    string ModelName,
    TaskComplexity TaskComplexity,
    int PromptCharacterCount,
    int PromptThreshold);

internal static class ApiPromptCostGuard
{
public const string DashboardConfirmationQueryName = "confirmLargePaidApiPrompt";
public const string CliConfirmationFlag = "--confirm-large-paid-api-prompt";
private const int SimplePaidPromptThreshold = 4000;
private const int ComplexPaidPromptThreshold = 6000;

public static PaidApiPromptRisk? Evaluate(AgentTaskRunPreview preview)
{
    if (!ProviderSmokeRunner.IsPaidProviderName(preview.ProviderName))
    {
        return null;
    }

    var threshold = PromptThreshold(preview.TaskComplexity);
    return preview.PromptCharacterCount <= threshold
        ? null
        : new PaidApiPromptRisk(
            preview.ProviderName,
            preview.ModelName,
            preview.TaskComplexity,
            preview.PromptCharacterCount,
            threshold);
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

private static string BuildMessage(PaidApiPromptRisk risk, string confirmationInstruction)
{
    return $"Large paid API prompt requires explicit confirmation: {risk.ProviderName}/{risk.ModelName} {risk.TaskComplexity} prompt {risk.PromptCharacterCount} chars exceeds {risk.PromptThreshold}. {confirmationInstruction}.";
}

private static int PromptThreshold(TaskComplexity complexity)
{
    return complexity == TaskComplexity.Complex
        ? ComplexPaidPromptThreshold
        : SimplePaidPromptThreshold;
}
}
