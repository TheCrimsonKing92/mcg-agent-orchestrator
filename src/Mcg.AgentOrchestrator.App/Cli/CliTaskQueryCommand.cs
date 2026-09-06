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
            ExecuteAgainstKernelAndCommitCurrentGoal(
                args,
                new AgentOrchestratorKernel(),
                workspace,
                providers,
                channel,
                ref agents,
                ref workerProfiles,
                ref currentGoal,
                currentGoal);
            return;
        }

        // Preserve the dispatcher's validation order: help is handled first, then invalid flags
        // fail before any goal resolution or repository read.
        CliCommandHelp.ThrowIfInvalidFlags(args);
        var metadata = stateQueries.ListGoalMetadataAsync().GetAwaiter().GetResult();
        var target = ResolveTarget(args, stateQueries, metadata, currentGoal);
        ExecuteAgainstKernelAndCommitCurrentGoal(
            target.DispatchArgs,
            target.Kernel,
            workspace,
            providers,
            channel,
            ref agents,
            ref workerProfiles,
            ref currentGoal,
            target.Goal);
    }

    private static TaskQueryTarget ResolveTarget(
        IReadOnlyList<string> args,
        IOrchestratorStateQueries stateQueries,
        IReadOnlyList<GoalSummary> metadata,
        Goal? currentGoal)
    {
        if (!args[0].Equals("task", StringComparison.OrdinalIgnoreCase))
            return ResolveImplicitTarget(args, stateQueries, metadata, currentGoal);

        if (args.Count > 1 && args[1].Equals("--goal", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Count < 4)
                return new TaskQueryTarget(new AgentOrchestratorKernel(), null, args);

            var explicitTarget = LoadGoalMatches(stateQueries, metadata, args[2]);
            var explicitGoal = ResolveExplicitGoal(explicitTarget, args[2]);
            return new TaskQueryTarget(explicitTarget, explicitGoal, args);
        }

        if (!HasInlineGoalPrefixShape(args))
            return ResolveImplicitTarget(args, stateQueries, metadata, currentGoal);

        var inlineKernel = LoadGoalMatches(stateQueries, metadata, args[1]);
        if (inlineKernel.Goals.Count == 1)
        {
            var inlineGoal = inlineKernel.Goals.Single();
            return new TaskQueryTarget(
                inlineKernel,
                inlineGoal,
                [args[0], "--goal", inlineGoal.Id.Value, .. args.Skip(2)]);
        }

        var implicitTarget = ResolveImplicitTarget(args, stateQueries, metadata, currentGoal);
        var dispatchArgs = implicitTarget.Goal is null
            ? args
            : [args[0], "--goal", implicitTarget.Goal.Id.Value, args[1], .. args.Skip(2)];
        return implicitTarget with { DispatchArgs = dispatchArgs };
    }

    private static TaskQueryTarget ResolveImplicitTarget(
        IReadOnlyList<string> args,
        IOrchestratorStateQueries stateQueries,
        IReadOnlyList<GoalSummary> metadata,
        Goal? currentGoal)
    {
        var candidates = metadata
            .Select((goal, index) => (Goal: goal, Index: index))
            .OrderByDescending(candidate => candidate.Goal.CreatedAt ?? DateTimeOffset.MinValue)
            .ThenBy(candidate => candidate.Index)
            .Select(candidate => candidate.Goal)
            .ToList();
        if (currentGoal is not null)
        {
            var current = candidates.FirstOrDefault(goal =>
                goal.Id.Equals(currentGoal.Id.Value, StringComparison.OrdinalIgnoreCase));
            if (current is not null)
            {
                candidates.Remove(current);
                candidates.Insert(0, current);
            }
        }

        foreach (var candidate in candidates)
        {
            var goalId = new GoalId(candidate.Id);
            var kernel = stateQueries.LoadGoalsAsync([goalId]).GetAwaiter().GetResult();
            var goal = kernel.Goals.SingleOrDefault(loaded => loaded.Id == goalId);
            if (goal is not null)
                return new TaskQueryTarget(kernel, goal, args);
        }

        return new TaskQueryTarget(new AgentOrchestratorKernel(), null, args);
    }

    private static AgentOrchestratorKernel LoadGoalMatches(
        IOrchestratorStateQueries stateQueries,
        IReadOnlyList<GoalSummary> metadata,
        string prefix)
    {
        var goalIds = FindGoalMatches(metadata, prefix)
            .Select(summary => new GoalId(summary.Id))
            .ToArray();
        return goalIds.Length == 0
            ? new AgentOrchestratorKernel()
            : stateQueries.LoadGoalsAsync(goalIds).GetAwaiter().GetResult();
    }

    private static Goal ResolveExplicitGoal(AgentOrchestratorKernel kernel, string prefix)
    {
        var matches = kernel.Goals
            .Where(goal => goal.Id.Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0],
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

    private static void ExecuteAgainstKernelAndCommitCurrentGoal(
        IReadOnlyList<string> args,
        AgentOrchestratorKernel commandKernel,
        OrchestratorWorkspace workspace,
        IModelProviderRegistry providers,
        IOperatorChannel? channel,
        ref IReadOnlyList<AgentDefinition> agents,
        ref WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal,
        Goal? initialCurrentGoal)
    {
        var commandCurrentGoal = initialCurrentGoal;
        ExecuteAgainstKernel(
            args,
            commandKernel,
            workspace,
            providers,
            channel,
            ref agents,
            ref workerProfiles,
            ref commandCurrentGoal);
        currentGoal = commandCurrentGoal;
    }

    private sealed record TaskQueryTarget(
        AgentOrchestratorKernel Kernel,
        Goal? Goal,
        IReadOnlyList<string> DispatchArgs);
}
