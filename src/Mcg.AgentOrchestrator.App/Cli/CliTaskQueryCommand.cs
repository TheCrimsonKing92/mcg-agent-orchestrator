using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliTaskQueryCommand
{
    internal static bool IsTaskQueryCommand(IReadOnlyList<string> args) =>
        args.Count >= 1 &&
        (args[0].Equals("task", StringComparison.OrdinalIgnoreCase) ||
         args[0].Equals("tasks", StringComparison.OrdinalIgnoreCase));

    internal static void Execute(
        IReadOnlyList<string> args,
        IOrchestratorStateQueries stateQueries,
        OrchestratorWorkspace workspace,
        IModelProviderRegistry providers,
        IOperatorChannel? channel,
        ref IReadOnlyList<AgentDefinition> agents,
        ref WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal)
    {
        if (CliCommandHelp.IsCommandSpecificHelp(args))
        {
            ExecuteAgainstKernel(
                args,
                new AgentOrchestratorKernel(),
                workspace,
                providers,
                channel,
                ref agents,
                ref workerProfiles,
                ref currentGoal);
            return;
        }

        var metadata = stateQueries.ListGoalMetadataAsync().GetAwaiter().GetResult();
        var target = ResolveTarget(args, metadata, currentGoal);
        if (target.GoalId is null)
        {
            ExecuteAgainstKernel(
                target.DispatchArgs,
                new AgentOrchestratorKernel(),
                workspace,
                providers,
                channel,
                ref agents,
                ref workerProfiles,
                ref currentGoal);
            return;
        }

        var commandKernel = stateQueries.LoadGoalsAsync([target.GoalId]).GetAwaiter().GetResult();
        var loadedGoal = commandKernel.Goals.SingleOrDefault(goal => goal.Id == target.GoalId);
        if (loadedGoal is null)
        {
            throw new KeyNotFoundException(
                $"Goal '{target.MissingGoalSelector ?? target.GoalId.Value}' was not found.");
        }

        Goal? commandCurrentGoal = loadedGoal;
        ExecuteAgainstKernel(
            target.DispatchArgs,
            commandKernel,
            workspace,
            providers,
            channel,
            ref agents,
            ref workerProfiles,
            ref commandCurrentGoal);
        currentGoal = commandCurrentGoal;
    }

    private static TaskQueryTarget ResolveTarget(
        IReadOnlyList<string> args,
        IReadOnlyList<GoalSummary> metadata,
        Goal? currentGoal)
    {
        var implicitGoalId = ResolveImplicitGoalId(metadata, currentGoal);
        if (!args[0].Equals("task", StringComparison.OrdinalIgnoreCase))
            return new TaskQueryTarget(implicitGoalId, args, implicitGoalId?.Value);

        if (args.Count > 1 && args[1].Equals("--goal", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Count < 4)
                return new TaskQueryTarget(implicitGoalId, args, implicitGoalId?.Value);

            var goalId = ResolveExplicitGoalId(metadata, args[2]);
            return new TaskQueryTarget(goalId, args, args[2]);
        }

        if (!HasInlineGoalPrefixShape(args))
            return new TaskQueryTarget(implicitGoalId, args, implicitGoalId?.Value);

        var inlineMatches = FindGoalMatches(metadata, args[1]);
        if (inlineMatches.Length == 1)
        {
            var goalId = new GoalId(inlineMatches[0].Id);
            return new TaskQueryTarget(
                goalId,
                [args[0], "--goal", goalId.Value, .. args.Skip(2)],
                args[1]);
        }

        IReadOnlyList<string> dispatchArgs = implicitGoalId is null
            ? args
            : [args[0], "--goal", implicitGoalId.Value, args[1], .. args.Skip(2)];
        return new TaskQueryTarget(implicitGoalId, dispatchArgs, implicitGoalId?.Value);
    }

    private static GoalId? ResolveImplicitGoalId(IReadOnlyList<GoalSummary> metadata, Goal? currentGoal)
    {
        var current = currentGoal is null
            ? null
            : metadata.FirstOrDefault(goal =>
                goal.Id.Equals(currentGoal.Id.Value, StringComparison.OrdinalIgnoreCase));
        if (current is not null)
            return new GoalId(current.Id);

        var latest = metadata
            .Select((goal, index) => (Goal: goal, Index: index))
            .OrderByDescending(candidate => candidate.Goal.CreatedAt ?? DateTimeOffset.MinValue)
            .ThenBy(candidate => candidate.Index)
            .Select(candidate => candidate.Goal)
            .FirstOrDefault();
        return latest is null ? null : new GoalId(latest.Id);
    }

    private static GoalId ResolveExplicitGoalId(IReadOnlyList<GoalSummary> metadata, string prefix)
    {
        var matches = FindGoalMatches(metadata, prefix);
        return matches.Length switch
        {
            1 => new GoalId(matches[0].Id),
            0 => throw new KeyNotFoundException($"Goal '{prefix}' was not found."),
            _ => throw new InvalidOperationException($"Goal prefix '{prefix}' is ambiguous.")
        };
    }

    private static GoalSummary[] FindGoalMatches(IReadOnlyList<GoalSummary> metadata, string prefix) =>
        metadata
            .Where(goal => goal.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    private static bool HasInlineGoalPrefixShape(IReadOnlyList<string> args) =>
        args.Count > 2 &&
        !args[1].StartsWith("--", StringComparison.Ordinal) &&
        !args[2].StartsWith("--", StringComparison.Ordinal) &&
        (!int.TryParse(args[1], out _) || args[1].Length >= 8);

    private static void ExecuteAgainstKernel(
        IReadOnlyList<string> args,
        AgentOrchestratorKernel commandKernel,
        OrchestratorWorkspace workspace,
        IModelProviderRegistry providers,
        IOperatorChannel? channel,
        ref IReadOnlyList<AgentDefinition> agents,
        ref WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal)
    {
        var changed = CliCommandDispatcher.ExecuteCommand(
            args,
            commandKernel,
            workspace,
            ref agents,
            providers,
            ref workerProfiles,
            ref currentGoal,
            channel);
        if (changed)
        {
            throw new InvalidOperationException(
                "Task query attempted to mutate orchestrator state through its read-only route.");
        }
    }

    private sealed record TaskQueryTarget(
        GoalId? GoalId,
        IReadOnlyList<string> DispatchArgs,
        string? MissingGoalSelector);
}
