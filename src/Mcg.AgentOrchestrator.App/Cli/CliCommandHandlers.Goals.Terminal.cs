using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCommandHandlers
{
private static Goal HandleGoalStopCommand(CliExecutionContext context, IReadOnlyList<string> parts, bool supersede)
{
    var goal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts[1]);
    if (RequiresGoalStopConfirmation(context.CurrentGoal, goal) && !HasGoalStopConfirmation(parts))
    {
        throw new InvalidOperationException($"{parts[0]} requires --confirm-goal-stop for active or non-current goals.");
    }

    var textParts = RemoveStandaloneFlag(parts, "--confirm-goal-stop");
    var reason = ResolveTextArgument(textParts, inlineIndex: 2, $"{parts[0]} <goal-id-prefix> <reason> [--confirm-goal-stop] | {parts[0]} <goal-id-prefix> --text-file <path> [--confirm-goal-stop]", "--text-file");
    return supersede
        ? context.Kernel.SupersedeGoal(goal.Id, reason)
        : context.Kernel.CancelGoal(goal.Id, reason);
}

private static Goal HandleGoalParkCommand(CliExecutionContext context, IReadOnlyList<string> parts)
{
    var goal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts[1]);
    var textParts = RemoveStandaloneFlag(parts, "--confirm-goal-park");
    var reason = ResolveTextArgument(textParts, inlineIndex: 2, "park-goal <goal-id-prefix> <reason> [--confirm-goal-park] | park-goal <goal-id-prefix> --text-file <path> [--confirm-goal-park]", "--text-file");
    if (string.IsNullOrWhiteSpace(reason))
    {
        throw new ArgumentException("Goal park reason cannot be empty.", nameof(parts));
    }

    var runningTasks = goal.Tasks.Where(task => task.LastProcess is { IsRunning: true }).ToArray();
    if (!HasCliConfirmation(parts, "--confirm-goal-park") &&
        !parts[2].Contains("--confirm-goal-park", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"Goal park dry run {goal.Id.Value[..8]}:");
        Console.WriteLine($"  reason: {reason}");
        Console.WriteLine($"  running dispatches to cancel: {runningTasks.Length}");
        Console.WriteLine("  attention waits: resolve with park reason");
        Console.WriteLine($"  command: park-goal {goal.Id.Value[..8]} <reason> --confirm-goal-park");
        return goal;
    }

    var runner = new BackgroundDispatchRunner();
    foreach (var task in runningTasks)
    {
        _ = runner.CancelLatestProcess(context.Kernel, goal.Id, task.Id);
    }

    var resolvedHumanWaits = context.Kernel.HumanInputRequests.Count(request =>
        request.GoalId == goal.Id &&
        !request.IsCompleted &&
        HumanWaitPolicyDefaults.BlocksActiveWork(request.Kind));
    _ = context.Kernel.ParkGoal(goal.Id, reason);
    var store = CollaborationItemStore.ForDirectory(context.Workspace.OrchestratorDirectory);
    var resolvedAttentionItems = store.ResolveOpenForGoalAsync(goal.Id.Value, $"Goal parked: {reason}").GetAwaiter().GetResult();
    Console.WriteLine($"Goal parked {goal.Id.Value[..8]}.");
    Console.WriteLine($"Cancelled running dispatches: {runningTasks.Length}");
    Console.WriteLine($"Resolved human waits: {resolvedHumanWaits}");
    Console.WriteLine($"Resolved attention items: {resolvedAttentionItems}");
    return goal;
}

private static Goal HandleGoalUnparkCommand(CliExecutionContext context, IReadOnlyList<string> parts)
{
    var goal = OrchestratorEntityResolver.ResolveGoal(context.Kernel, context.CurrentGoal, parts[1]);
    var textParts = RemoveStandaloneFlag(parts, "--confirm-goal-unpark");
    var reason = ResolveTextArgument(textParts, inlineIndex: 2, "unpark-goal <goal-id-prefix> <reason> [--confirm-goal-unpark] | unpark-goal <goal-id-prefix> --text-file <path> [--confirm-goal-unpark]", "--text-file");
    if (string.IsNullOrWhiteSpace(reason))
    {
        throw new ArgumentException("Goal unpark reason cannot be empty.", nameof(parts));
    }

    if (goal.Status != GoalStatus.Parked)
    {
        throw new InvalidOperationException(
            $"unpark-goal only applies to Parked goals; goal '{goal.Id.Value[..8]}' is {goal.Status}. " +
            "No state changed. Use status <goal> to inspect the current lifecycle state.");
    }

    if (!HasCliConfirmation(parts, "--confirm-goal-unpark") &&
        !parts[2].Contains("--confirm-goal-unpark", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"Goal unpark dry run {goal.Id.Value[..8]}:");
        Console.WriteLine($"  reason: {reason}");
        Console.WriteLine($"  status change: Parked -> Active");
        Console.WriteLine($"  command: unpark-goal {goal.Id.Value[..8]} <reason> --confirm-goal-unpark");
        return goal;
    }

    _ = context.Kernel.UnparkGoal(goal.Id, reason);
    Console.WriteLine($"Goal unparked {goal.Id.Value[..8]}.");
    Console.WriteLine("Status change: Parked -> Active");
    return goal;
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
