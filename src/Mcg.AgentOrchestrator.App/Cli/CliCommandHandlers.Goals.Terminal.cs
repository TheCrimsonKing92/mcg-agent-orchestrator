using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCommandHandlers
{
    private static bool ExecuteGoalCancelInMemory(CliExecutionContext context, GoalCancelCommand command)
    {
        var goal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, command.GoalSelector);
        var outcome = ApplyGoalCancelWithoutRendering(command, context.Kernel, goal.Id, context.CurrentGoal?.Id);
        if (outcome.Goal is not null) context.CurrentGoal = outcome.Goal;
        RenderGoalCancelOutcome(outcome, context.Workspace);
        return outcome.ShouldSave;
    }

    private static bool ExecuteGoalSupersedeInMemory(CliExecutionContext context, GoalSupersedeCommand command)
    {
        var goal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, command.GoalSelector);
        var outcome = ApplyGoalSupersedeWithoutRendering(command, context.Kernel, goal.Id, context.CurrentGoal?.Id);
        if (outcome.Goal is not null) context.CurrentGoal = outcome.Goal;
        RenderGoalSupersedeOutcome(outcome, context.Workspace);
        return outcome.ShouldSave;
    }

    private static bool ExecuteGoalParkInMemory(CliExecutionContext context, GoalParkCommand command)
    {
        var goal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, command.GoalSelector);
        var outcome = ApplyGoalParkWithoutRendering(command, context.Kernel, goal.Id);
        if (outcome.Goal is not null) context.CurrentGoal = outcome.Goal;
        RenderGoalParkOutcome(command, outcome, context.Workspace);
        return outcome.ShouldSave;
    }

    private static bool ExecuteGoalUnparkInMemory(CliExecutionContext context, GoalUnparkCommand command)
    {
        var goal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, command.GoalSelector);
        var outcome = ApplyGoalUnparkWithoutRendering(command, context.Kernel, goal.Id);
        if (outcome.Goal is not null) context.CurrentGoal = outcome.Goal;
        RenderGoalUnparkOutcome(command, outcome, context.Workspace);
        return outcome.ShouldSave;
    }

    private static bool ExecuteGoalAbandonInMemory(CliExecutionContext context, GoalAbandonCommand command)
    {
        var goal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, command.GoalSelector);
        var hooks = context.CleanupContext.Hooks;
        var outcome = ApplyGoalAbandonWithoutRendering(command, context.Kernel, goal.Id, context.Workspace, hooks);
        if (outcome.Goal is not null) context.CurrentGoal = outcome.Goal;
        RenderGoalAbandonOutcome(command, outcome, context.Kernel, context.Workspace, hooks);
        return outcome.ShouldSave;
    }

private static bool RequiresGoalStopConfirmation(Goal? currentGoal, Goal goal)
{
    var isCurrent = currentGoal is not null && currentGoal.Id == goal.Id;
    return !isCurrent || goal.Status == GoalStatus.Active;
}

private static bool HasGoalStopConfirmation(IReadOnlyList<string> parts) =>
    parts.Any(part => part.Equals("--confirm-goal-stop", StringComparison.OrdinalIgnoreCase) ||
        part.Contains("--confirm-goal-stop", StringComparison.OrdinalIgnoreCase));

private static IReadOnlyList<string> RemoveStandaloneFlag(IReadOnlyList<string> parts, string flag) =>
    parts.Where(part => !part.Equals(flag, StringComparison.OrdinalIgnoreCase)).ToList();

private static bool HandleGoalsPrune(CliExecutionContext context, IReadOnlyList<string> parts)
{
    if (!context.Worktrees.IsGitWorkTree(context.Workspace.ExecutionDirectory))
    {
        throw new InvalidOperationException(
            "goals-prune requires a git work tree; the execution directory is not inside a git repository.");
    }

    var confirm = HasCliConfirmation(parts, "--confirm-prune");
    var plan = confirm
        ? GoalsPrunePlanner.Apply(context.Kernel, context.Workspace.ExecutionDirectory, context.CleanupContext.Hooks)
        : GoalsPrunePlanner.Build(context.Kernel, context.Workspace.ExecutionDirectory);

    ConsoleViews.PrintGoalsPrunePlan(plan);
    return plan.PrunedCount > 0;
}
}
