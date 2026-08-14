using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class WorkerContextHelpers
{
    public static string TrimArtifactBlock(string value, int maxChars)
    {
        var trimmed = value.Trim();
        if (trimmed.Length <= maxChars)
        {
            return trimmed;
        }

        return trimmed[..maxChars] +
            Environment.NewLine +
            $"...[truncated {trimmed.Length - maxChars} chars for context artifact budget]...";
    }

    public static string TrimArtifactBlock(string value, int maxChars, bool preserveCompleteArtifact) =>
        preserveCompleteArtifact ? value : TrimArtifactBlock(value, maxChars);

    internal static bool UsesTypedContextPackage(AgentRole role, string? providerName, string? modelName) => role is
        AgentRole.Researcher or
        AgentRole.Planner or
        AgentRole.Developer or
        AgentRole.Tester or
        AgentRole.Reviewer &&
        string.Equals(providerName, "OpenAI", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(modelName, AgentCatalog.OpenAiSolSubscriptionModelAlias, StringComparison.OrdinalIgnoreCase);
}
