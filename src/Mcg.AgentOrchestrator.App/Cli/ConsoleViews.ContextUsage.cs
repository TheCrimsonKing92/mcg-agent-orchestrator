using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
    private static void PrintContextUsageLine(TaskSpec task)
    {
        var receipt = task.DispatchHistory.LastOrDefault(dispatch => dispatch.ContextPackageReceipt is not null)
            ?.ContextPackageReceipt;
        if (receipt is null)
            return;

        Console.WriteLine(FormatContextUsageLine(receipt));
    }

    internal static string FormatContextUsageLine(WorkerContextPackageReceipt receipt) =>
        $"Context usage: input={ContextUsageFigures.Provider(receipt.InputTokens).Display}, " +
        $"cached={ContextUsageFigures.Provider(receipt.CachedInputTokens).Display}, " +
        $"uncached={ContextUsageFigures.Uncached(receipt).Display}, " +
        $"output={ContextUsageFigures.Provider(receipt.OutputTokens).Display}, " +
        $"prompt-bytes={ContextUsageFigures.PromptBytes(receipt).Display}, " +
        $"estimate={ContextUsageFigures.Estimate(receipt).Display}, " +
        $"harness-overhead={ContextUsageFigures.HarnessOverhead(receipt).Display}";
}
