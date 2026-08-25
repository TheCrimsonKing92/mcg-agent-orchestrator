using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal sealed record HistoricalTrialSelector(
    string GoalId,
    string? TaskId = null,
    int? DispatchIndex = null);

internal sealed record HistoricalTrialReplayResolution(
    string BaseCommit,
    TrialWorkload Workload);

internal enum TrialComparisonUnavailableReason
{
    InvalidSourceMode,
    InvalidExplicitWorkload,
    ReservedEnvironmentCollision,
    HistoricalResolverUnavailable,
    InvalidHistoricalSelector,
    HistoricalGoalNotFound,
    HistoricalGoalNotTerminal,
    HistoricalSelectorMismatch,
    HistoricalBriefUnavailable,
    HistoricalBaseCommitUnavailable,
    HistoricalModelUnavailable,
    HistoricalTupleAmbiguous
}

internal sealed class TrialComparisonUnavailableException(
    TrialComparisonUnavailableReason reason,
    string message) : InvalidOperationException(message)
{
    public TrialComparisonUnavailableReason Reason { get; } = reason;
}

internal static class HistoricalTrialReplayResolver
{
    public static HistoricalTrialReplayResolution Resolve(
        AgentOrchestratorKernel kernel,
        HistoricalTrialSelector selector)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        ValidateSelector(selector);
        var goalId = new GoalId(selector.GoalId.Trim());
        GoalSnapshot snapshot;
        GoalTimingReportSnapshot timing;
        try
        {
            snapshot = kernel.ExportGoalSnapshot(goalId);
            timing = kernel.BuildGoalTimingReport(goalId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        {
            throw Unavailable(
                TrialComparisonUnavailableReason.HistoricalGoalNotFound,
                $"Historical goal '{selector.GoalId}' was not found in durable state.",
                ex);
        }

        return Resolve(snapshot, selector, timing);
    }

    internal static HistoricalTrialReplayResolution Resolve(
        GoalSnapshot goal,
        HistoricalTrialSelector selector,
        GoalTimingReportSnapshot timing)
    {
        ArgumentNullException.ThrowIfNull(goal);
        ArgumentNullException.ThrowIfNull(timing);
        ValidateSelector(selector);
        if (!goal.Id.Equals(selector.GoalId, StringComparison.OrdinalIgnoreCase))
        {
            throw Unavailable(
                TrialComparisonUnavailableReason.HistoricalGoalNotFound,
                $"Historical goal '{selector.GoalId}' was not found in durable state.");
        }

        if (goal.Status is not (GoalStatus.Completed or GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded))
        {
            throw Unavailable(
                TrialComparisonUnavailableReason.HistoricalGoalNotTerminal,
                $"Historical goal '{goal.Id}' is not terminal (status={goal.Status}).");
        }

        var tasks = string.IsNullOrWhiteSpace(selector.TaskId)
            ? goal.Tasks
            : goal.Tasks
                .Where(task => task.Id.Equals(selector.TaskId, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        if (tasks.Count == 0)
        {
            throw Unavailable(
                TrialComparisonUnavailableReason.HistoricalSelectorMismatch,
                $"Historical task selector '{selector.TaskId}' matched no task in goal '{goal.Id}'.");
        }

        var candidates = new List<Candidate>();
        foreach (var task in tasks)
        {
            var dispatches = task.DispatchHistory ?? [];
            if (selector.DispatchIndex is { } dispatchIndex)
            {
                if (dispatchIndex >= dispatches.Count)
                {
                    throw Unavailable(
                        TrialComparisonUnavailableReason.HistoricalSelectorMismatch,
                        $"Historical dispatch index {dispatchIndex} matched no dispatch for task '{task.Id}'.");
                }

                candidates.Add(CreateCandidate(goal, task, dispatches[dispatchIndex], dispatchIndex));
            }
            else
            {
                candidates.AddRange(dispatches.Select((dispatch, index) => CreateCandidate(goal, task, dispatch, index)));
            }
        }

        if (candidates.Count == 0)
        {
            throw Unavailable(
                TrialComparisonUnavailableReason.HistoricalSelectorMismatch,
                $"Historical selector matched no dispatch in goal '{goal.Id}'.");
        }

        var tuples = candidates
            .Select(candidate => candidate.Tuple)
            .Distinct()
            .ToArray();
        if (tuples.Length != 1)
        {
            throw Unavailable(
                TrialComparisonUnavailableReason.HistoricalTupleAmbiguous,
                $"Historical selector resolved {tuples.Length} distinct brief/base/model tuples; select one task and dispatch explicitly.");
        }

        var tuple = tuples[0];
        var provenance = $"historical:goal={goal.Id};task={selector.TaskId ?? "*"};dispatch={selector.DispatchIndex?.ToString() ?? "*"}";
        var briefDigest = TrialIdentity.ComputeBriefDigest(tuple.BriefContent);
        return new HistoricalTrialReplayResolution(
            tuple.BaseCommit,
            new TrialWorkload(
                $"goal:{goal.Id}:brief:{tuple.BriefVersion}",
                tuple.BriefContent,
                briefDigest,
                $"{tuple.ProviderName}/{tuple.ModelName}",
                provenance,
                timing));
    }

    private static Candidate CreateCandidate(
        GoalSnapshot goal,
        TaskSnapshot task,
        TaskDispatchSnapshot dispatch,
        int dispatchIndex)
    {
        var briefContent = dispatch.BriefSnapshot;
        if (briefContent is null)
        {
            briefContent = goal.BriefVersions?
                .SingleOrDefault(version => version.Version == dispatch.BriefVersion)
                ?.Text;
        }

        if (briefContent is null)
        {
            throw Unavailable(
                TrialComparisonUnavailableReason.HistoricalBriefUnavailable,
                $"Historical dispatch {dispatchIndex} for task '{task.Id}' has no exact brief snapshot or recorded brief version {dispatch.BriefVersion}.");
        }

        if (string.IsNullOrWhiteSpace(dispatch.BaseCommit))
        {
            throw Unavailable(
                TrialComparisonUnavailableReason.HistoricalBaseCommitUnavailable,
                $"Historical dispatch {dispatchIndex} for task '{task.Id}' has no recorded base commit.");
        }

        if (string.IsNullOrWhiteSpace(dispatch.ProviderName) || string.IsNullOrWhiteSpace(dispatch.ModelName))
        {
            throw Unavailable(
                TrialComparisonUnavailableReason.HistoricalModelUnavailable,
                $"Historical dispatch {dispatchIndex} for task '{task.Id}' has no complete provider/model evidence.");
        }

        return new Candidate(new CandidateTuple(
            dispatch.BriefVersion,
            briefContent,
            dispatch.BaseCommit.Trim().ToLowerInvariant(),
            dispatch.ProviderName.Trim(),
            dispatch.ModelName.Trim()));
    }

    private static void ValidateSelector(HistoricalTrialSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        if (string.IsNullOrWhiteSpace(selector.GoalId)
            || selector.DispatchIndex < 0
            || (selector.DispatchIndex is not null && string.IsNullOrWhiteSpace(selector.TaskId)))
        {
            throw Unavailable(
                TrialComparisonUnavailableReason.InvalidHistoricalSelector,
                "Historical replay requires goalId; dispatchIndex is zero-based and requires taskId.");
        }
    }

    private static TrialComparisonUnavailableException Unavailable(
        TrialComparisonUnavailableReason reason,
        string message,
        Exception? inner = null) =>
        inner is null
            ? new TrialComparisonUnavailableException(reason, message)
            : new TrialComparisonUnavailableException(reason, $"{message} {inner.Message}");

    private sealed record Candidate(CandidateTuple Tuple);

    private sealed record CandidateTuple(
        int BriefVersion,
        string BriefContent,
        string BaseCommit,
        string ProviderName,
        string ModelName);
}
