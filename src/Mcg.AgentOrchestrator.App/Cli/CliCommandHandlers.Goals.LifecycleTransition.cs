using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCommandHandlers
{
    internal const string GoalUnparkUsage =
        "unpark-goal <goal-id-prefix> <reason> [--confirm-goal-unpark] | unpark-goal <goal-id-prefix> --text-file <path> [--confirm-goal-unpark]";

    internal enum GoalLifecycleTransitionDisposition
    {
        Applied,
        AppliedWithLiveDispatches,
        DryRun,
        Rejected,
        ConflictExhausted
    }

    internal sealed record GoalUnparkCommand(
        string GoalSelector,
        string Reason,
        bool Confirmed);

    internal sealed record GoalLifecycleTransitionOutcome(
        GoalLifecycleTransitionDisposition Disposition,
        GoalId GoalId,
        GoalStatus? ObservedStatus,
        Goal? Goal = null,
        ProgressEvent? CommittedTimelineEvent = null,
        string? RejectionReason = null,
        IReadOnlyList<GoalLiveDispatch>? LiveDispatches = null,
        int? ResolvedHumanWaits = null)
    {
        internal bool ShouldSave => Disposition is
            GoalLifecycleTransitionDisposition.Applied or
            GoalLifecycleTransitionDisposition.AppliedWithLiveDispatches;
    }

    internal static GoalUnparkCommand PrepareGoalUnparkCommand(IReadOnlyList<string> parts)
    {
        CliArgumentParser.RequirePartCount(parts, 3, GoalUnparkUsage);
        var textParts = RemoveStandaloneFlag(parts, "--confirm-goal-unpark");
        var reason = ResolveTextArgument(textParts, inlineIndex: 2, GoalUnparkUsage, "--text-file");
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Goal unpark reason cannot be empty.", nameof(parts));
        }

        return new GoalUnparkCommand(
            parts[1],
            reason,
            HasCliConfirmation(parts, "--confirm-goal-unpark") ||
                parts[2].Contains("--confirm-goal-unpark", StringComparison.OrdinalIgnoreCase));
    }

    internal static GoalLifecycleTransitionOutcome ApplyGoalUnparkWithoutRendering(
        GoalUnparkCommand command,
        AgentOrchestratorKernel kernel,
        GoalId goalId)
    {
        var goal = kernel.GetGoal(goalId);
        if (goal.Status != GoalStatus.Parked)
        {
            return new GoalLifecycleTransitionOutcome(
                GoalLifecycleTransitionDisposition.Rejected,
                goal.Id,
                goal.Status,
                goal,
                RejectionReason:
                    $"unpark-goal only applies to Parked goals; goal '{goal.Id.Value[..8]}' is {goal.Status}. " +
                    "No state changed. Use status <goal> to inspect the current lifecycle state.");
        }

        if (!command.Confirmed)
        {
            return new GoalLifecycleTransitionOutcome(
                GoalLifecycleTransitionDisposition.DryRun,
                goal.Id,
                goal.Status,
                goal);
        }

        _ = kernel.UnparkGoal(goal.Id, command.Reason);
        return new GoalLifecycleTransitionOutcome(
            GoalLifecycleTransitionDisposition.Applied,
            goal.Id,
            GoalStatus.Active,
            goal,
            goal.Timeline[^1]);
    }

    internal static void RenderGoalUnparkOutcome(
        GoalUnparkCommand command,
        GoalLifecycleTransitionOutcome outcome,
        OrchestratorWorkspace workspace,
        Action? deliverCommittedLifecycleEvent = null)
    {
        switch (outcome.Disposition)
        {
            case GoalLifecycleTransitionDisposition.Applied:
                if (outcome.CommittedTimelineEvent is null)
                {
                    throw new InvalidOperationException("Committed unpark outcome is missing its timeline event.");
                }

                Console.WriteLine($"Goal unparked {outcome.GoalId.Value[..8]}.");
                Console.WriteLine("Status change: Parked -> Active");
                if (deliverCommittedLifecycleEvent is null)
                    AppendCommittedLifecycleEvent(outcome, workspace, GoalStatus.Active);
                else
                    deliverCommittedLifecycleEvent();

                return;

            case GoalLifecycleTransitionDisposition.DryRun:
                Console.WriteLine($"Goal unpark dry run {outcome.GoalId.Value[..8]}:");
                Console.WriteLine($"  reason: {command.Reason}");
                Console.WriteLine("  status change: Parked -> Active");
                Console.WriteLine($"  command: unpark-goal {outcome.GoalId.Value[..8]} <reason> --confirm-goal-unpark");
                return;

            case GoalLifecycleTransitionDisposition.Rejected:
                throw new InvalidOperationException(outcome.RejectionReason ?? "The goal unpark transition was rejected.");

            case GoalLifecycleTransitionDisposition.ConflictExhausted:
                throw CreateConflictExhaustedException("unpark-goal", outcome.GoalId);

            default:
                throw new InvalidOperationException($"Unsupported lifecycle transition outcome: {outcome.Disposition}.");
        }
    }

    internal static void AppendCommittedLifecycleEvent(
        GoalLifecycleTransitionOutcome outcome,
        OrchestratorWorkspace workspace,
        GoalStatus committedStatus)
    {
        if (outcome.CommittedTimelineEvent is null)
        {
            throw new InvalidOperationException("Committed lifecycle outcome is missing its timeline event.");
        }

        try
        {
            new Mcg.AgentOrchestrator.Infrastructure.GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory, integrationBranch: workspace.IntegrationBranch)
                .AppendTimelineEvent(outcome.CommittedTimelineEvent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new GoalLifecycleProjectionException(
                $"Goal '{outcome.GoalId.Value[..8]}' is committed {committedStatus}, but lifecycle event projection failed: {ex.Message}", ex);
        }
    }

    internal static InvalidOperationException CreateConflictExhaustedException(string command, GoalId goalId) =>
        new($"{command} could not commit goal '{goalId.Value[..8]}' because concurrent updates exhausted the retry budget. " +
            $"No {command[..^5]} success was reported. Inspect status and retry the command.");

    internal sealed class GoalLifecycleProjectionException(string message, Exception innerException)
        : IOException(message, innerException);
}
