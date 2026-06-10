using System.Text;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Dashboard.Rendering;

public static partial class GoalTranscriptRenderer
{
    public static string Render(AgentOrchestratorKernel kernel, Goal goal)
    {
        var monitor = kernel.BuildMonitor(goal.Id);
        var nextActions = kernel.BuildNextActions(goal.Id);
        var acceptance = kernel.BuildGoalAcceptanceSummary(goal.Id);
        var evidence = kernel.BuildGoalEvidenceSummary(goal.Id);
        var stages = kernel.BuildStageReadinessReport(goal.Id);
        var verificationGate = kernel.BuildVerificationGate(goal.Id);
        var verificationWorklist = kernel.BuildVerificationWorklist(goal.Id);
        var humanInputWorklist = kernel.BuildHumanInputWorklist(goal.Id);
        var text = new StringBuilder();

        text.AppendLine($"# Goal {goal.Id.Value[..8]}");
        text.AppendLine();
        text.AppendLine($"Objective: {goal.Objective}");
        text.AppendLine($"Status: {Display(goal.Status)}");
        text.AppendLine($"Tasks: {goal.Tasks.Count}");
        text.AppendLine($"Pending human input: {monitor.PendingHumanInputCount}");
        if (monitor.LastTimelineEventAt is not null)
        {
            text.AppendLine($"Last event: {monitor.LastTimelineEventAt:u}");
        }

        text.AppendLine();
        text.AppendLine("## Task Status");
        foreach (var count in monitor.TaskStatusCounts)
        {
            text.AppendLine($"- {Display(count.Status)}: {count.Count}");
        }

        text.AppendLine();
        text.AppendLine("## Recommended Next Steps");
        for (var index = 0; index < nextActions.Items.Count; index++)
        {
            var item = nextActions.Items[index];
            text.AppendLine($"{index + 1}. {Display(item.Kind)}: {item.Message}");
            text.AppendLine($"   Suggested command: {BuildSuggestedCommand(goal, item)}");
        }

        text.AppendLine();
        text.AppendLine("## Needs Attention");
        if (monitor.AttentionItems.Count == 0)
        {
            text.AppendLine("- none");
        }
        else
        {
            foreach (var item in monitor.AttentionItems)
            {
                var task = item.TaskId is null ? "goal" : $"task {GetTaskDisplayNumber(goal, item.TaskId)}";
                text.AppendLine($"- {Display(item.Kind)} ({task}): {item.Message}");
            }
        }

        text.AppendLine();
        text.AppendLine("## Human Decisions");
        text.AppendLine($"Open: {humanInputWorklist.OpenCount}");
        if (humanInputWorklist.Items.Count == 0)
        {
            text.AppendLine("- none");
        }
        else
        {
            foreach (var item in humanInputWorklist.Items)
            {
                var scope = item.TaskId is null
                    ? "goal"
                    : $"task {GetTaskDisplayNumber(goal, item.TaskId)} [{Display(item.TaskStatus!.Value)}] {item.Role}";
                text.AppendLine($"- {item.RequestId.Value[..8]} ({scope}): {item.Question}");
                text.AppendLine($"  Suggested command: {BuildHumanInputSuggestedCommand(item.RequestId)}");
            }
        }

        text.AppendLine();
        text.AppendLine("## Goal Completion");
        text.AppendLine($"Accepted: {acceptance.IsAccepted}");
        text.AppendLine($"Tasks passed: {acceptance.PassedTasks}/{acceptance.TotalTasks}");
        text.AppendLine($"Open verification: {acceptance.OpenVerificationCount}");
        text.AppendLine($"Pending human input: {acceptance.PendingHumanInputCount}");
        if (acceptance.Blockers.Count == 0)
        {
            text.AppendLine("- none");
        }
        else
        {
            foreach (var blocker in acceptance.Blockers)
            {
                int? taskNumber = blocker.TaskId is null ? null : GetTaskDisplayNumber(goal, blocker.TaskId);
                var scope = taskNumber is null ? "goal" : $"task {taskNumber}";
                text.AppendLine($"- {Display(blocker.Kind)} ({scope}): {blocker.Message}");
                text.AppendLine($"  Suggested command: {BuildAcceptanceSuggestedCommand(blocker, taskNumber)}");
            }
        }

        text.AppendLine();
        text.AppendLine("## Recorded Proof");
        text.AppendLine($"Tasks: {evidence.TotalTasks}");
        text.AppendLine($"Execution: {evidence.TasksWithExecution}; dispatch: {evidence.TasksWithDispatch}; process: {evidence.TasksWithProcess} (running {evidence.RunningProcesses})");
        text.AppendLine($"Tokens: {FormatTokenUsage(evidence.InputTokens, evidence.OutputTokens)}");
        text.AppendLine($"Potentially paid tokens: {FormatTokenUsage(evidence.PotentiallyPaidInputTokens, evidence.PotentiallyPaidOutputTokens)}");
        if (evidence.ModelUsage.Count > 0)
        {
            text.AppendLine("Model usage:");
            foreach (var usage in evidence.ModelUsage)
            {
                text.AppendLine($"- {FormatModelUsage(usage)}");
            }
        }
        if (evidence.DispatchModelUsage.Count > 0)
        {
            text.AppendLine("Dispatch models:");
            foreach (var dispatch in evidence.DispatchModelUsage)
            {
                text.AppendLine($"- {FormatDispatchModelUsage(dispatch)}");
            }
        }
        text.AppendLine($"Verification: {evidence.TasksWithVerification}; passed={evidence.PassedVerifications}; failed={evidence.FailedVerifications}");
        text.AppendLine($"Pending human input: {evidence.PendingHumanInputCount}");
        foreach (var item in evidence.Tasks)
        {
            text.AppendLine($"- Task {GetTaskDisplayNumber(goal, item.TaskId)} [{Display(item.LatestEvidence)}] {item.Role}: {item.Message}");
        }

        text.AppendLine();
        text.AppendLine("## Task Readiness");
        text.AppendLine($"Ready for acceptance: {stages.IsReadyForAcceptance}");
        text.AppendLine($"Verified: {stages.VerifiedStages}/{stages.TotalStages}");
        text.AppendLine($"Open: {stages.OpenStages}");
        text.AppendLine($"Blocked: {stages.BlockedStages}");
        foreach (var stage in stages.Stages)
        {
            var taskNumber = GetTaskDisplayNumber(goal, stage.TaskId);
            text.AppendLine($"- Task {taskNumber} [{Display(stage.StageStatus)}] {stage.Stage}: {stage.Message}");
            text.AppendLine($"  Suggested command: {BuildStageSuggestedCommand(taskNumber, stage)}");
        }

        text.AppendLine();
        text.AppendLine("## Verification Status");
        text.AppendLine($"Satisfied: {verificationGate.IsSatisfied}");
        foreach (var gate in verificationGate.Tasks)
        {
            text.AppendLine($"- Task {GetTaskDisplayNumber(goal, gate.TaskId)} [{Display(gate.GateStatus)}] {gate.Role}: {gate.Message}");
        }

        text.AppendLine();
        text.AppendLine("## Verification To-Do");
        text.AppendLine($"Open: {verificationWorklist.OpenCount}");
        if (verificationWorklist.Items.Count == 0)
        {
            text.AppendLine("- none");
        }
        else
        {
            foreach (var item in verificationWorklist.Items)
            {
                var taskNumber = GetTaskDisplayNumber(goal, item.TaskId);
                text.AppendLine($"- Task {taskNumber} [{Display(item.GateStatus)}] {item.Role}: {item.SuggestedAction}");
                text.AppendLine($"  Suggested command: {BuildVerificationSuggestedCommand(taskNumber, item.GateStatus)}");
            }
        }

        text.AppendLine();
        text.AppendLine("## Tasks");
        for (var index = 0; index < goal.Tasks.Count; index++)
        {
            RenderTask(text, goal, index + 1, goal.Tasks[index]);
        }

        text.AppendLine();
        text.AppendLine("## Goal Activity Log");
        foreach (var evt in goal.Timeline.OrderBy(evt => evt.OccurredAt))
        {
            var task = evt.TaskId is null ? "goal" : $"task {GetTaskDisplayNumber(goal, evt.TaskId)}";
            text.AppendLine($"- {evt.OccurredAt:u} {Display(evt.Kind)} ({task}): {evt.Message}");
        }

        return text.ToString();
    }

}


