using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class BacklogSimilarityCorpus
{
    internal static List<SimilarityDocument> Load(CliExecutionContext context)
    {
        var corpus = BacklogSimilaritySearch.LoadBacklogDocumentsAsync(context.Workspace.BacklogStorePath)
            .GetAwaiter().GetResult().ToList();
        corpus.AddRange(context.BacklogReads?.SimilarityDocuments() ?? context.Kernel.Goals
            .Where(goal => goal.Status == GoalStatus.Completed)
            .Select(goal => new SimilarityDocument(
                "goal",
                goal.Id.Value,
                goal.Status.ToString(),
                FirstNonEmptyLine(goal.Objective),
                goal.MetadataTerminatedAt ?? goal.MetadataCreatedAt ??
                    goal.Timeline.OrderBy(item => item.OccurredAt).LastOrDefault()?.OccurredAt ?? DateTimeOffset.MinValue,
                goal.Objective)).ToArray());
        return corpus;
    }

    private static string FirstNonEmptyLine(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? "-";
}
