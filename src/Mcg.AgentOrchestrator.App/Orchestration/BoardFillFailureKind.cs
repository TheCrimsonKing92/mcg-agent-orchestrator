using System.ComponentModel;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class BoardFillFailureKind
{
    internal const string PreModel = "pre-model";
    internal const string ModelRound = "model-round";

    internal static string Classify(Exception exception, bool processSeamEntered) =>
        !processSeamEntered || exception is Win32Exception ||
        // WorkerProcessRunner reports a null Process.Start result with this exact fault.
        exception is InvalidOperationException { Message: "Failed to start worker process." }
            ? PreModel : ModelRound;

    // Unknown legacy kinds preserve their existing failure allowance.
    internal static bool CountsTowardItem(BoardFillDraftRound round) =>
        round.Outcome == "failed" && round.FailureKind != PreModel;
}
