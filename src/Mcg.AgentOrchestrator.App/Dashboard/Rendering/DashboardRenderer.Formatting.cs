using System.Net;
using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Dashboard.Rendering;

public static partial class DashboardRenderer
{
    private static string RenderEvidence(TaskSpec task)
    {
        var execution = task.LastExecution is null
            ? string.Empty
            : RenderExecutionSummary(task.LastExecution);

        if (task.LastVerification is not null)
        {
            var cls = task.LastVerification.Succeeded ? "ok" : "bad";
            var history = task.VerificationHistory.Count == 1
                ? "1 verification"
                : $"{task.VerificationHistory.Count} verifications";
            return $"<div class=\"{cls}\">Verification exit {task.LastVerification.ExitCode}: {Encode(task.LastVerification.Command)} <span class=\"meta\">({history})</span></div>{execution}";
        }

        if (DispatchFailureClassifier.HasRecoverableSubscriptionLimitHistory(task))
        {
            var latest = task.VerificationHistory.Last();
            var retry = DispatchFailureClassifier.TryGetSubscriptionLimitRetryAfter(task, out var retryAfter)
                ? $" Retry after <code>{Encode(retryAfter.ToString("u"))}</code>."
                : string.Empty;
            return $"<div class=\"attention\"><strong>Retry later</strong><br>Recoverable subscription usage limit.{retry} <span class=\"meta\">exit {latest.ExitCode} &middot; {Encode(latest.Command)}</span></div>";
        }

        if (task.LastProcess is not null)
        {
            return task.LastProcess.IsRunning
                ? $"<div>Process running: pid {task.LastProcess.ProcessId}</div>"
                : $"<div>Process exit {Encode(task.LastProcess.ExitCode?.ToString() ?? "n/a")}</div>";
        }

        if (task.LastDispatch is not null)
        {
            return $"<div>Dispatch: {Encode(task.LastDispatch.WorkerName)} &middot; {Encode(task.LastDispatch.Command)}</div>";
        }

        if (task.LastExecution is not null)
        {
            return $"{execution}<pre>{Encode(task.LastExecution.Output)}</pre>";
        }

        return "<span class=\"meta\">none</span>";
    }

    private static string RenderExecutionSummary(TaskExecutionRecord execution)
    {
        var inputTokens = execution.Usage?.InputTokens?.ToString() ?? "n/a";
        var outputTokens = execution.Usage?.OutputTokens?.ToString() ?? "n/a";

        return $"<div>Model: {Encode(execution.ProviderName)}/{Encode(execution.ModelName)} by {Encode(execution.AgentName)} <span class=\"meta\">tokens {inputTokens} in / {outputTokens} out &middot; stop reason {Encode(execution.StopReason)}</span></div>";
    }

    private static string RenderVerificationPlan(TaskSpec task)
    {
        return string.IsNullOrWhiteSpace(task.VerificationPlan)
            ? "<span class=\"meta\">none</span>"
            : Encode(task.VerificationPlan);
    }

    private static string FormatTokenUsage(int? inputTokens, int? outputTokens)
    {
        return $"{inputTokens?.ToString() ?? "n/a"} in / {outputTokens?.ToString() ?? "n/a"} out";
    }

    private static string RenderTaskGate(TaskVerificationGate gate)
    {
        var cls = gate.GateStatus == VerificationGateStatus.Passed ? "ok" : "bad";
        return $"<div class=\"{cls}\">{Encode(Display(gate.GateStatus))}</div><span class=\"meta\">{Encode(gate.Message)}</span>";
    }

    private static string RenderWorkItemReference(
        Goal goal,
        TaskId? taskId,
        AgentRole? role = null,
        WorkTaskStatus? status = null,
        string? description = null)
    {
        if (taskId is null)
        {
            return $"<strong>Goal</strong><br><span class=\"meta\">{Encode(goal.Objective)}</span>";
        }

        var task = goal.Tasks.FirstOrDefault(task => task.Id == taskId);
        if (task is not null)
        {
            var taskNumber = GetTaskDisplayNumber(goal, task.Id);
            return $"<strong>Task {taskNumber}: {task.RequiredRole}</strong><br><span class=\"meta\">{Encode(Display(task.Status))}</span><br>{Encode(task.Description)}";
        }

        var roleText = role?.ToString() ?? "Task";
        var statusText = status is null ? "unknown status" : Display(status.Value);
        var descriptionText = string.IsNullOrWhiteSpace(description) ? taskId.Value[..8] : description;
        return $"<strong>{Encode(roleText)}</strong><br><span class=\"meta\">{Encode(statusText)}</span><br>{Encode(descriptionText)}";
    }

    private static string RenderNextActionControl(Goal goal, NextActionItem item, DashboardRenderOptions options)
    {
        var control = DashboardNextActionControls.Build(goal, item, options.HealthReport?.Agents);
        if (control is null)
        {
            if (item.Kind == NextActionKind.AnswerHumanInput && item.HumanInputRequestId is not null)
            {
                return $"<a class=\"button-link primary\" data-next-action=\"{item.Kind}\" href=\"#{Encode(BuildHumanInputFormAnchor(item.HumanInputRequestId))}\">Answer</a>";
            }

            return "<span class=\"meta\">Manual</span>";
        }

        return control.Method.Equals("POST", StringComparison.OrdinalIgnoreCase)
            ? $"<button type=\"button\" data-next-action=\"{item.Kind}\" data-action-button=\"{Encode(control.Url)}\">{Encode(control.Label)}</button>"
            : $"<a data-next-action=\"{item.Kind}\" href=\"{Encode(control.Url)}\" target=\"_blank\" rel=\"noreferrer\">{Encode(control.Label)}</a>";
    }

    private static string BuildSuggestedCommand(Goal goal, NextActionItem item)
    {
        string? taskNumber = null;
        if (item.TaskId is not null)
        {
            taskNumber = GetTaskDisplayNumber(goal, item.TaskId).ToString();
        }

        return item.Kind switch
        {
            NextActionKind.AnswerHumanInput => item.HumanInputRequestId is null
                ? "pending"
                : $"answer {item.HumanInputRequestId.Value[..8]} <answer>",
            NextActionKind.InspectFailedTask => taskNumber is null ? "monitor" : $"task {taskNumber} | retry {taskNumber} <note>",
            NextActionKind.FixFailedVerification => taskNumber is null ? "monitor" : $"verifications {taskNumber} | retry {taskNumber} <note>",
            NextActionKind.RefreshRunningProcess => taskNumber is null ? "monitor" : $"refresh-dispatch {taskNumber}",
            NextActionKind.ExecuteRecordedDispatch => taskNumber is null ? "monitor" : $"execute-dispatch {taskNumber}",
            NextActionKind.VerifyCompletedTask => taskNumber is null ? "monitor" : $"verify {taskNumber} <command> | verify-manual {taskNumber} passed <note>",
            NextActionKind.RunAssignedTask => taskNumber is null ? "monitor" : $"run {taskNumber} | subscription-dispatch {taskNumber}",
            NextActionKind.DelegatePendingTask => "delegate",
            NextActionKind.MonitorGoal => "monitor",
            _ => "monitor"
        };
    }

    private static string BuildVerificationSuggestedCommand(int taskNumber, VerificationGateStatus gateStatus)
    {
        return gateStatus switch
        {
            VerificationGateStatus.NotReady => $"task {taskNumber}",
            VerificationGateStatus.MissingVerification => $"verify {taskNumber} <command> | verify-manual {taskNumber} passed <note>",
            VerificationGateStatus.FailedVerification => $"verifications {taskNumber} | retry {taskNumber} <note>",
            VerificationGateStatus.Passed => "gates",
            _ => "monitor"
        };
    }

    private static string BuildHumanInputSuggestedCommand(HumanInputRequestId requestId)
    {
        return $"answer {requestId.Value[..8]} <answer>";
    }

    private static string BuildHumanInputFormAnchor(HumanInputRequestId requestId)
    {
        return $"input-{requestId.Value[..8]}";
    }

    private static string BuildStageSuggestedCommand(int taskNumber, TaskStageReadiness stage)
    {
        return stage.StageStatus switch
        {
            StageReadinessStatus.NeedsDelegation => "delegate",
            StageReadinessStatus.ReadyToRun => $"run {taskNumber} | subscription-dispatch {taskNumber} | profile-dispatch {taskNumber} <profile-name>",
            StageReadinessStatus.InProgress when stage.LatestEvidence == TaskEvidenceKind.RunningProcess => $"refresh-dispatch {taskNumber}",
            StageReadinessStatus.InProgress when stage.LatestEvidence == TaskEvidenceKind.Dispatch => $"execute-dispatch {taskNumber}",
            StageReadinessStatus.InProgress => $"task {taskNumber}",
            StageReadinessStatus.WaitingForHuman => "input-needed",
            StageReadinessStatus.NeedsVerification => $"verify {taskNumber} <command> | verify-manual {taskNumber} passed <note>",
            StageReadinessStatus.VerificationFailed => $"verifications {taskNumber} | retry {taskNumber} <note>",
            StageReadinessStatus.Verified => "stages",
            StageReadinessStatus.FailedOrCancelled => $"retry {taskNumber} <note>",
            _ => "monitor"
        };
    }

    private static string BuildAcceptanceSuggestedCommand(GoalAcceptanceBlocker blocker, int? taskNumber)
    {
        return blocker.Kind switch
        {
            GoalAcceptanceBlockerKind.PendingHumanInput when blocker.HumanInputRequestId is not null =>
                BuildHumanInputSuggestedCommand(blocker.HumanInputRequestId),
            GoalAcceptanceBlockerKind.VerificationNotReady when taskNumber is not null =>
                $"task {taskNumber}",
            GoalAcceptanceBlockerKind.VerificationMissing when taskNumber is not null =>
                $"verify {taskNumber} <command> | verify-manual {taskNumber} passed <note>",
            GoalAcceptanceBlockerKind.VerificationFailed when taskNumber is not null =>
                $"verifications {taskNumber} | retry {taskNumber} <note>",
            _ => "monitor"
        };
    }

    private static int GetTaskDisplayNumber(Goal goal, TaskId taskId)
    {
        for (var index = 0; index < goal.Tasks.Count; index++)
        {
            if (goal.Tasks[index].Id == taskId)
            {
                return index + 1;
            }
        }

        throw new KeyNotFoundException($"Task '{taskId}' was not found.");
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}


