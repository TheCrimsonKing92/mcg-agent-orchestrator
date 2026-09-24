using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCommandHandlers
{
    internal const string GoalParkUsage =
        "park-goal <goal-id-prefix> <reason> [--confirm-goal-park] | park-goal <goal-id-prefix> --text-file <path> [--confirm-goal-park]";

    internal sealed record GoalParkCommand(string GoalSelector, string Reason, bool Confirmed);
    internal sealed record GoalLiveDispatch(int TaskNumber, AgentRole Role, TaskId TaskId, int ProcessId);

    internal static GoalParkCommand PrepareGoalParkCommand(IReadOnlyList<string> parts)
    {
        CliArgumentParser.RequirePartCount(parts, 3, GoalParkUsage);
        var textParts = RemoveStandaloneFlag(parts, "--confirm-goal-park");
        var reason = ResolveTextArgument(textParts, inlineIndex: 2, GoalParkUsage, "--text-file");
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Goal park reason cannot be empty.", nameof(parts));
        }

        return new GoalParkCommand(
            parts[1], reason,
            HasCliConfirmation(parts, "--confirm-goal-park") ||
                parts[2].Contains("--confirm-goal-park", StringComparison.OrdinalIgnoreCase));
    }

    internal static GoalLifecycleTransitionOutcome ApplyGoalParkWithoutRendering(
        GoalParkCommand command,
        AgentOrchestratorKernel kernel,
        GoalId goalId)
    {
        var goal = kernel.GetGoal(goalId);
        var liveDispatches = goal.Tasks
            .Select((task, index) => (Task: task, Number: index + 1))
            .Where(item => item.Task.LastProcess is { IsRunning: true })
            .Select(item => new GoalLiveDispatch(
                item.Number, item.Task.RequiredRole, item.Task.Id, item.Task.LastProcess!.ProcessId))
            .OrderBy(item => item.TaskNumber)
            .ToArray();

        if (!command.Confirmed)
        {
            return new GoalLifecycleTransitionOutcome(
                GoalLifecycleTransitionDisposition.DryRun, goal.Id, goal.Status, goal,
                LiveDispatches: liveDispatches);
        }

        var resolvedHumanWaits = kernel.HumanInputRequests.Count(request =>
            request.GoalId == goal.Id &&
            !request.IsCompleted &&
            HumanWaitPolicyDefaults.BlocksActiveWork(request.Kind));
        _ = kernel.ParkGoal(goal.Id, command.Reason);
        return new GoalLifecycleTransitionOutcome(
            liveDispatches.Length == 0
                ? GoalLifecycleTransitionDisposition.Applied
                : GoalLifecycleTransitionDisposition.AppliedWithLiveDispatches,
            goal.Id, GoalStatus.Parked, goal,
            LiveDispatches: liveDispatches,
            ResolvedHumanWaits: resolvedHumanWaits);
    }

    internal static void RenderGoalParkOutcome(
        GoalParkCommand command,
        GoalLifecycleTransitionOutcome outcome,
        OrchestratorWorkspace workspace)
    {
        switch (outcome.Disposition)
        {
            case GoalLifecycleTransitionDisposition.Applied:
            case GoalLifecycleTransitionDisposition.AppliedWithLiveDispatches:
                if (outcome.ResolvedHumanWaits is null || outcome.LiveDispatches is null)
                {
                    throw new InvalidOperationException("Committed park outcome is missing its dispatch or wait counts.");
                }

                var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
                var resolvedAttentionItems = store.ResolveOpenForGoalAsync(
                    outcome.GoalId.Value, $"Goal parked: {command.Reason}").GetAwaiter().GetResult();
                Console.WriteLine($"Goal parked {outcome.GoalId.Value[..8]}.");
                if (outcome.Disposition == GoalLifecycleTransitionDisposition.AppliedWithLiveDispatches)
                {
                    Console.WriteLine($"Live dispatches still running: {outcome.LiveDispatches.Count}");
                    WriteLiveDispatches(outcome.LiveDispatches);
                }
                else
                {
                    Console.WriteLine("Cancelled running dispatches: 0");
                }

                Console.WriteLine($"Resolved human waits: {outcome.ResolvedHumanWaits.Value}");
                Console.WriteLine($"Resolved attention items: {resolvedAttentionItems}");
                return;

            case GoalLifecycleTransitionDisposition.DryRun:
                Console.WriteLine($"Goal park dry run {outcome.GoalId.Value[..8]}:");
                Console.WriteLine($"  reason: {command.Reason}");
                if (outcome.LiveDispatches is { Count: > 0 })
                {
                    Console.WriteLine($"  live dispatches still running after park: {outcome.LiveDispatches.Count}");
                    WriteLiveDispatches(outcome.LiveDispatches);
                }
                else
                {
                    Console.WriteLine("  running dispatches to cancel: 0");
                }

                Console.WriteLine("  attention waits: resolve with park reason");
                Console.WriteLine($"  command: park-goal {outcome.GoalId.Value[..8]} <reason> --confirm-goal-park");
                return;

            case GoalLifecycleTransitionDisposition.ConflictExhausted:
                throw new InvalidOperationException(
                    $"park-goal could not commit goal '{outcome.GoalId.Value[..8]}' because concurrent updates exhausted the retry budget. " +
                    "No park success was reported. Inspect status and retry the command.");

            default:
                throw new InvalidOperationException($"Unsupported park transition outcome: {outcome.Disposition}.");
        }
    }

    private static void WriteLiveDispatches(IReadOnlyList<GoalLiveDispatch> dispatches)
    {
        foreach (var dispatch in dispatches)
        {
            Console.WriteLine($"  task {dispatch.TaskNumber} {dispatch.Role} {dispatch.TaskId.Value[..8]}: pid {dispatch.ProcessId} still running");
        }
    }
}
