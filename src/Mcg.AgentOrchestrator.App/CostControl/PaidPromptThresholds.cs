using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.CostControl;

internal static class PaidPromptThresholds
{
    public const int SimplePaidPrompt = 6000;
    public const int ComplexPaidPrompt = 9500;
    public const int BatchPaidPrompt = 18000;
    public const int BatchPaidTasks = 3;
    public const int PriorTaskEvidenceAllowance = 2000;

    public static int PromptThreshold(TaskComplexity? complexity, bool usesComplexModel)
    {
        return complexity == TaskComplexity.Complex || usesComplexModel
            ? ComplexPaidPrompt
            : SimplePaidPrompt;
    }

    public static int EffectivePromptCharacterCount(int promptCharacterCount, int priorTaskEvidenceCharacterCount)
    {
        return Math.Max(0, promptCharacterCount - Math.Min(priorTaskEvidenceCharacterCount, PriorTaskEvidenceAllowance));
    }
}
