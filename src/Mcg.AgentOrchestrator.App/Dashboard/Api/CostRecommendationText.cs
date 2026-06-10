namespace Mcg.AgentOrchestrator.App.Dashboard.Api;

internal static class CostRecommendationText
{
    public static string LocalModelSwitchAction =>
        $"try local Ollama/{ProviderModelDefaults.Ollama} via agent configuration";

    public static string PaidApiOverkill(string providerName, string modelName) =>
        $"Prior evidence says {providerName}/{modelName} was overkill; {LocalModelSwitchAction} before paid API run.";

    public static string PaidStartOverkill(string providerName, string modelName) =>
        $"Prior evidence says {providerName}/{modelName} was overkill; {LocalModelSwitchAction} before paid start.";

    public static string PaidSubscriptionOverkill() =>
        $"Prior evidence says this subscription model was overkill; {LocalModelSwitchAction} before paid subscription start.";
}
