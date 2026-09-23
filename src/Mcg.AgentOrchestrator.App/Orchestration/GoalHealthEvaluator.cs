using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

public enum GoalHealthDisposition
{
    Healthy,
    Active,
    ReadyForAcceptance,
    ProviderLimited,
    NeedsOperator,
    Blocked
}

public sealed record GoalHealthReport(
    GoalId GoalId,
    string GoalPrefix,
    GoalHealthDisposition Disposition,
    int Score,
    string Recommendation,
    string SuggestedCommand,
    IReadOnlyList<string> Reasons);

public static class GoalHealthEvaluator
{
    public static GoalHealthReport Build(
        AgentOrchestratorKernel kernel,
        Goal goal,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog workerProfiles,
        string executionDirectory,
        AutonomyPolicy policy)
    {
        var prefix = goal.Id.Value[..8];
        var nextAction = kernel.BuildNextActions(goal.Id).Items.FirstOrDefault();
        var recovery = GoalRecoveryPlanner.Build(kernel, goal, executionDirectory);
        var acceptance = AcceptanceQueuePlanner.Build(kernel, executionDirectory, policy)
            .Items
            .FirstOrDefault(item => item.GoalId == goal.Id);
        var capacity = SubscriptionPlanBuilder.Build(
            goal,
            agents,
            workerProfiles,
            task => WorkerProfileDispatcher.EstimateSubscriptionPromptCharacters(kernel, goal, task, agents),
            providerHoldScope: kernel.Goals).CapacitySchedule;
        var reasons = new List<string>();

        if (recovery.WorktreeDirty == true)
        {
            reasons.Add("worktree dirty");
            return Report(GoalHealthDisposition.Blocked, 20, "Inspect and resolve dirty worktree state before more automation.", $"goal-recovery {prefix}");
        }

        var deferredTask = goal.Tasks.FirstOrDefault(task =>
            DispatchFailureClassifier.IsSubscriptionRetryDeferred(task, DateTimeOffset.UtcNow, out _));
        if (deferredTask is not null &&
            DispatchFailureClassifier.IsSubscriptionRetryDeferred(deferredTask, DateTimeOffset.UtcNow, out var deferredUntil))
        {
            reasons.Add($"subscription retry-after {deferredUntil:u}");
            return Report(GoalHealthDisposition.ProviderLimited, 55, $"Wait until {deferredUntil:u} or route deferred work to an alternate provider.", $"subscription-plan {prefix}");
        }

        var failedTask = goal.Tasks.FirstOrDefault(task =>
            task.Status is not WorkTaskStatus.Completed and not WorkTaskStatus.Cancelled &&
            (task.Status == WorkTaskStatus.Failed ||
            (task.LastVerification is { } verification &&
             DispatchFailureClassifier.Classify(task, verification).Kind != DispatchOutcomeKind.VerifiedSuccess)));
        if (failedTask is not null)
        {
            var failedFinding = recovery.TaskFindings.FirstOrDefault(finding => finding.TaskId == failedTask.Id);
            var taskNumber = goal.Tasks.TakeWhile(task => task.Id != failedTask.Id).Count() + 1;
            reasons.Add(failedFinding?.Finding ?? $"task {taskNumber} has typed status {failedTask.Status}");
            return Report(
                GoalHealthDisposition.Blocked,
                25,
                "Repair or retry the failed task before continuing.",
                failedFinding?.SuggestedCommand ?? $"retry {taskNumber} <note>");
        }

        var staleProcess = recovery.TaskFindings.FirstOrDefault(finding => finding.RecoveryDecision is not null);
        if (staleProcess is not null)
        {
            reasons.Add(staleProcess.Finding);
            return Report(GoalHealthDisposition.NeedsOperator, 40, "Refresh recorded dispatch state before starting new work.", staleProcess.SuggestedCommand);
        }

        if (recovery.PendingHumanInputCount > 0)
        {
            reasons.Add("pending human input");
            return Report(GoalHealthDisposition.NeedsOperator, 45, "Answer pending human input before resuming automation.", "input-needed");
        }

        if (capacity.DeferredCount > 0 &&
            capacity.Disposition is ProviderCapacityDisposition.Deferred or ProviderCapacityDisposition.Blocked)
        {
            reasons.Add($"provider capacity {capacity.Disposition}");
            return Report(GoalHealthDisposition.ProviderLimited, 55, capacity.Recommendation, $"subscription-plan {prefix}");
        }

        if (acceptance is { Disposition: AcceptanceQueueDisposition.Ready })
        {
            reasons.Add("acceptance queue ready");
            return Report(GoalHealthDisposition.ReadyForAcceptance, 85, "Review acceptance evidence and merge the goal branch.", acceptance.SuggestedCommand);
        }

        if (nextAction is not null)
        {
            if (nextAction.Kind == NextActionKind.MonitorGoal)
            {
                reasons.Add("monitor only");
                return Report(GoalHealthDisposition.Healthy, 90, "Monitor goal; no intervention is currently recommended.", BuildSuggestedCommand(goal, nextAction));
            }

            reasons.Add($"next action {nextAction.Kind}");
            return Report(GoalHealthDisposition.Active, 70, nextAction.Message, BuildSuggestedCommand(goal, nextAction));
        }

        reasons.Add("no actionable issues detected");
        return Report(GoalHealthDisposition.Healthy, 90, "Monitor goal; no intervention is currently recommended.", $"monitor {prefix}");

        GoalHealthReport Report(GoalHealthDisposition disposition, int score, string recommendation, string command)
        {
            return new GoalHealthReport(
                goal.Id,
                prefix,
                disposition,
                score,
                OutputTextPreview.CreateTimeline(recommendation).Text,
                command,
                reasons.Select(reason => OutputTextPreview.CreateTimeline(reason).Text).ToArray());
        }
    }

    private static string BuildSuggestedCommand(Goal goal, NextActionItem item)
    {
        int? taskNumber = item.TaskId is null
            ? null
            : goal.Tasks.Select((task, index) => new { task.Id, Number = index + 1 })
                .FirstOrDefault(task => task.Id == item.TaskId)?.Number;
        return item.Kind switch
        {
            NextActionKind.AnswerHumanInput => item.HumanInputRequestId is null ? "input-needed" : item.ResumeCommand ?? $"answer {item.HumanInputRequestId.Value[..8]} <answer>",
            NextActionKind.InspectFailedTask => taskNumber is null ? "monitor" : $"task {taskNumber} | retry {taskNumber} <note>",
            NextActionKind.FixFailedVerification => taskNumber is null ? "monitor" : $"verifications {taskNumber} | retry {taskNumber} <note>",
            NextActionKind.RefreshRunningProcess => taskNumber is null ? "monitor" : $"refresh-dispatch {taskNumber}",
            NextActionKind.ExecuteRecordedDispatch => taskNumber is null ? "monitor" : $"execute-dispatch {taskNumber} --confirm-dispatch-start",
            NextActionKind.VerifyCompletedTask => taskNumber is null ? "monitor" : $"verify {taskNumber} <command> | verify-manual {taskNumber} passed <note>",
            NextActionKind.RunAssignedTask => taskNumber is null ? "monitor" : $"run {taskNumber}",
            NextActionKind.DelegatePendingTask => "delegate",
            NextActionKind.MonitorGoal => $"monitor {goal.Id.Value[..8]}",
            _ => $"monitor {goal.Id.Value[..8]}"
        };
    }
}
