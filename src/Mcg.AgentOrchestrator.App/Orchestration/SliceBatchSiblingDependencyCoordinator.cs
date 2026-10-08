using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Sibling edges carry completed stream code, rather than requiring individual child landings.
internal sealed class SliceBatchSiblingDependencyCoordinator(string? executionDirectory)
{
    internal static bool IsSiblingEdge(Goal consumer, Goal producer) =>
        consumer.SliceBatchParentId is not null &&
        consumer.SliceBatchParentId == producer.SliceBatchParentId;

    internal static bool IsSatisfiedSibling(Goal consumer, Goal producer) =>
        IsSiblingEdge(consumer, producer) && consumer.DependsOn.Contains(producer.Id) &&
        SliceBatchParentExecutionGuard.IsStreamComplete(producer);

    internal static string DescribeHold(Goal producer) =>
        $"waiting on slice-batch sibling {producer.Id.Value[..8]} stream completion";

    internal static string? TryDescribeSiblingHold(Goal consumer, IReadOnlyCollection<Goal> goals) =>
        goals.FirstOrDefault(producer => IsSiblingEdge(consumer, producer) &&
            consumer.DependsOn.Contains(producer.Id) &&
            !SliceBatchParentExecutionGuard.IsStreamComplete(producer)) is { } incomplete
            ? DescribeHold(incomplete)
            : null;

    // Null is success. Git ancestry makes retries safe before dispatch history has been recorded.
    internal string? PrepareBranch(Goal consumer, IReadOnlyCollection<Goal> goals)
    {
        if (consumer.Tasks.Any(task => task.LastProcess is not null))
            return null;

        // Kernel goal insertion order is plan-node creation order, independent of edge list order.
        var producers = goals.Where(producer => IsSiblingEdge(consumer, producer) &&
            consumer.DependsOn.Contains(producer.Id)).ToArray();
        if (producers.Length == 0)
            return null;
        if (TryDescribeSiblingHold(consumer, goals) is { } hold)
            return hold;
        if (string.IsNullOrWhiteSpace(executionDirectory))
            return Failure(consumer, producers[0], "repository directory is unavailable");

        var currentProducer = producers[0];
        try
        {
            // Resolve immutable commit ids before changing the consumer branch.
            var tips = new List<(Goal Producer, string Tip)>();
            foreach (var producer in producers)
            {
                currentProducer = producer;
                var tip = GitCli.Run(executionDirectory, "rev-parse", "--verify",
                    $"refs/heads/{GoalWorktrees.BranchName(producer.Id)}^{{commit}}");
                if (!Succeeded(tip))
                    return Failure(consumer, producer, Diagnostic(tip));
                tips.Add((producer, tip.Output.Trim()));
            }

            var branch = GoalWorktrees.BranchName(consumer.Id);
            var exists = GitCli.Run(executionDirectory, "show-ref", "--verify", "--quiet", $"refs/heads/{branch}");
            if (!Succeeded(exists))
            {
                if (exists.ExitCode != 1 || exists.DrainTimedOut || !exists.ProcessStarted)
                    return Failure(consumer, currentProducer, Diagnostic(exists));
                var create = GitCli.Run(executionDirectory, "branch", branch, tips[0].Tip);
                if (!Succeeded(create))
                    return Failure(consumer, tips[0].Producer, Diagnostic(create));
            }

            string? worktree = null;
            foreach (var (producer, tip) in tips)
            {
                currentProducer = producer;
                var ancestry = GitCli.Run(executionDirectory, "merge-base", "--is-ancestor", tip, $"refs/heads/{branch}");
                if (Succeeded(ancestry))
                    continue;
                if (ancestry.ExitCode != 1 || ancestry.DrainTimedOut || !ancestry.ProcessStarted)
                    return Failure(consumer, producer, Diagnostic(ancestry));

                worktree ??= GoalWorktrees.TryResolve(executionDirectory, consumer.Id) ??
                    GoalWorktrees.Ensure(executionDirectory, consumer.Id);
                var status = GitCli.Run(worktree, "status", "--porcelain=v1", "--untracked-files=all");
                if (!Succeeded(status) || !string.IsNullOrWhiteSpace(status.Output))
                    return Failure(consumer, producer, $"consumer worktree must be clean: {Diagnostic(status)}");
                var mergeHead = GitCli.Run(worktree, "rev-parse", "--verify", "--quiet", "MERGE_HEAD");
                if (mergeHead.ExitCode != 1 || mergeHead.DrainTimedOut || !mergeHead.ProcessStarted)
                    return Failure(consumer, producer, "consumer worktree has an existing merge or merge state is unavailable");

                var merge = GitCli.Run(worktree, "merge", "--no-edit", tip);
                if (!Succeeded(merge))
                {
                    var conflicts = GitCli.Run(worktree, "diff", "--name-only", "--diff-filter=U");
                    var abort = GitCli.Run(worktree, "merge", "--abort");
                    return Failure(consumer, producer,
                        $"{Diagnostic(merge)}; conflicting paths: {conflicts.Output.Trim()}; abort: {Diagnostic(abort)}");
                }

                var contained = GitCli.Run(executionDirectory, "merge-base", "--is-ancestor", tip, $"refs/heads/{branch}");
                if (!Succeeded(contained))
                    return Failure(consumer, producer, $"producer commit missing after merge: {Diagnostic(contained)}");
            }
            return null;
        }
        catch (Exception exception)
        {
            return Failure(consumer, currentProducer, exception.Message);
        }
    }

    private static bool Succeeded(GitCli.GitResult result) =>
        result.Succeeded && result.ProcessStarted && !result.DrainTimedOut;

    private static string Diagnostic(GitCli.GitResult result) =>
        $"exit={result.ExitCode} drainTimedOut={result.DrainTimedOut} " +
        string.Join(" | ", new[] { result.Error, result.Output }
            .Where(text => !string.IsNullOrWhiteSpace(text)).Select(text => text.Trim().ReplaceLineEndings(" | ")));

    private static string Failure(Goal consumer, Goal producer, string detail) =>
        $"Slice-batch consumer {consumer.Id.Value[..8]} branch preparation held for producer {producer.Id.Value[..8]}: {detail}";
}
