using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.CostControl;

internal static class PaidPromptThresholds
{
    public const int SimplePaidPrompt = 4000;
    public const int ComplexPaidPrompt = 6000;
    public const int BatchPaidPrompt = 12000;
    public const int BatchPaidTasks = 3;

    public static int PromptThreshold(TaskComplexity? complexity, bool usesComplexModel)
    {
        return complexity == TaskComplexity.Complex || usesComplexModel
            ? ComplexPaidPrompt
            : SimplePaidPrompt;
    }
}
