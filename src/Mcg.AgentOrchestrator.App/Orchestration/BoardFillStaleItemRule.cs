using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class BoardFillStaleItemRule
{
    internal static string? ReadMainHead(string repositoryRoot, string integrationBranch)
    {
        var result = GitCli.Run(repositoryRoot, "rev-parse", "--verify", $"{integrationBranch}^{{commit}}");
        return result.Succeeded && !result.DrainTimedOut ? result.Output.Trim() : null;
    }

    internal static bool Excludes(BoardFillDraftRound round, BacklogItem item, string? currentMainHead) =>
        round.Outcome == "stale" && round.ChangeStamp >= BoardFillReadyItemSelector.ChangeStamp(item) &&
        !string.IsNullOrWhiteSpace(round.MainHead) &&
        (string.IsNullOrWhiteSpace(currentMainHead) ||
            string.Equals(round.MainHead, currentMainHead, StringComparison.OrdinalIgnoreCase));
}
