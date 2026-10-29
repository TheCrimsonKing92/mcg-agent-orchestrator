using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliGoalDependencyCommands
{
    internal static bool ExecuteGoalDepends(CliExecutionContext context, IReadOnlyList<string> parts)
    {
        CliArgumentParser.RequirePartCount(parts, 3, "goal-depends <goal-prefix> --on <dependency-prefix> | goal-depends <goal-prefix> --remove <dependency-prefix> | goal-depends <goal-prefix> --clear");
        var dependentGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts[1]);
        var onPrefix = CliCommandHandlers.GetFlagValue(parts, "--on");
        var removePrefix = CliCommandHandlers.GetFlagValue(parts, "--remove");
        var clear = CliCommandHandlers.HasCliConfirmation(parts, "--clear");
        if ((onPrefix is not null ? 1 : 0) + (removePrefix is not null ? 1 : 0) + (clear ? 1 : 0) != 1)
            throw new ArgumentException("goal-depends requires exactly one of --on, --remove, or --clear.");
        if (clear)
        {
            context.Kernel.ClearGoalDependencies(dependentGoal.Id);
            Console.WriteLine($"Dependencies cleared: {dependentGoal.Id.Value[..8]}");
            return true;
        }

        var dependencyGoal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, null, onPrefix ?? removePrefix!);
        if (onPrefix is not null)
        {
            context.Kernel.SetGoalDependency(dependentGoal.Id, dependencyGoal.Id);
            Console.WriteLine($"Dependency set: {dependentGoal.Id.Value[..8]} depends on {dependencyGoal.Id.Value[..8]}");
        }
        else
        {
            context.Kernel.RemoveGoalDependency(dependentGoal.Id, dependencyGoal.Id);
            Console.WriteLine($"Dependency removed: {dependentGoal.Id.Value[..8]} no longer depends on {dependencyGoal.Id.Value[..8]}");
        }
        return true;
    }

    internal static IReadOnlyList<Goal> ResolveAtCreation(CliExecutionContext context, IReadOnlyList<string> parts)
    {
        var prefixes = new List<string>();
        for (var index = 1; index < parts.Count; index++)
        {
            if (!parts[index].Equals("--depends-on", StringComparison.OrdinalIgnoreCase))
                continue;
            if (index + 1 >= parts.Count || string.IsNullOrWhiteSpace(parts[index + 1]) ||
                parts[index + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException("--depends-on requires a goal prefix.");
            prefixes.Add(parts[++index]);
        }

        if (prefixes.Count == 0)
            return [];
        if (parts.Any(part => part.Equals("--simple", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("--run", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("--from-backlog", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("--depends-on is supported only by goal <objective> --depends-on <goal-prefix> and goal --brief-file <path> --depends-on <goal-prefix>; it cannot be combined with --simple, --run or --from-backlog.");

        var dependencies = new List<Goal>();
        var seen = new HashSet<GoalId>();
        foreach (var prefix in prefixes)
        {
            var dependency = OrchestratorEntityResolver.ResolveGoal(context.Kernel, null, prefix);
            if (dependency.Status is GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded)
                throw new InvalidOperationException($"GOAL_DEPENDS_ON_REJECTED dependency={dependency.Id.Value[..8]} status={dependency.Status}");
            if (seen.Add(dependency.Id))
                dependencies.Add(dependency);
        }
        return dependencies;
    }

    internal static IReadOnlyCollection<string> ApplyAtCreation(AgentOrchestratorKernel kernel, Goal created, IReadOnlyList<Goal> dependencies)
    {
        // Capture derived edges before explicit edges mutate DependsOn, including overlapping edges.
        var preparedSourceBacklogDependencies = created.DependsOn.Select(id => id.Value).ToArray();
        foreach (var dependency in dependencies)
            kernel.SetGoalDependency(created.Id, dependency.Id);
        return preparedSourceBacklogDependencies;
    }
}
