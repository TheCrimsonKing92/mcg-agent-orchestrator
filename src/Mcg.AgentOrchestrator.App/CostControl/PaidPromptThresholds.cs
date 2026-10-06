using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.CostControl;

internal static class PaidPromptThresholds
{
    public const int SimplePaidPrompt = 6000;
    public const int ComplexPaidPrompt = 9500;
    public const int BatchPaidPrompt = 18000;
    public const int BatchPaidTasks = 3;
    public const int PriorTaskEvidenceAllowance = 2000;

    // A prompt is "anomalous" (likely brief-bloat / a bug) when it exceeds the size that is
    // PROPORTIONATE to its task complexity by this factor. A legitimately-large Complex brief (up
    // to AnomalyMultiplier x ComplexPaidPrompt) proceeds without nagging; only a disproportionate
    // prompt requires an explicit operator acknowledgement.
    public const int AnomalyMultiplier = 2;

    public static int PromptThreshold(TaskComplexity? complexity, bool usesComplexModel)
    {
        return complexity == TaskComplexity.Complex || usesComplexModel
            ? ComplexPaidPrompt
            : SimplePaidPrompt;
    }

    public static int PromptThreshold(
        string? providerName, string? modelName, TaskComplexity? complexity, bool usesComplexModel)
    {
        return string.Equals(providerName, "OpenAI", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(modelName, AgentCatalog.OpenAiGpt6LunaSubscriptionModelAlias, StringComparison.OrdinalIgnoreCase)
            ? ComplexPaidPrompt
            : PromptThreshold(complexity, usesComplexModel);
    }

    // The disproportionate-to-complexity ceiling: above this, a single prompt is treated as an
    // anomaly worth a human glance regardless of the autonomy posture.
    public static int AnomalyPromptThreshold(TaskComplexity? complexity, bool usesComplexModel)
    {
        return AnomalyMultiplier * PromptThreshold(complexity, usesComplexModel);
    }

    public static int AnomalyPromptThreshold(
        string? providerName, string? modelName, TaskComplexity? complexity, bool usesComplexModel)
    {
        return AnomalyMultiplier * PromptThreshold(providerName, modelName, complexity, usesComplexModel);
    }

    // A batch total this far over the soft batch ceiling is treated as anomalous fan-in.
    public static int AnomalyBatchThreshold => AnomalyMultiplier * BatchPaidPrompt;

    public static int EffectivePromptCharacterCount(int promptCharacterCount, int priorTaskEvidenceCharacterCount)
    {
        return Math.Max(0, promptCharacterCount - Math.Min(priorTaskEvidenceCharacterCount, PriorTaskEvidenceAllowance));
    }
}
