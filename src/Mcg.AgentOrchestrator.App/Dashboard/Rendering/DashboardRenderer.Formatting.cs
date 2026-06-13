using System.Net;
using System.Text;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Providers;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Rendering;
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
            var skills = ExtractWorkerResultSkills(task.LastVerification);
            var skillLine = skills.Length == 0
                ? string.Empty
                : $"<div class=\"meta\">Skills: {Encode(string.Join(", ", skills))}</div>";
            return $"<div class=\"{cls}\">Verification exit {task.LastVerification.ExitCode}: {Encode(task.LastVerification.Command)} <span class=\"meta\">({history})</span></div>{skillLine}{execution}";
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
            var heartbeat = RenderHeartbeatStatus(ProcessLogReader.ReadHeartbeat(task.LastProcess));
            return task.LastProcess.IsRunning
                ? $"<div>Process running: pid {task.LastProcess.ProcessId}</div>{heartbeat}"
                : $"<div>Process exit {Encode(task.LastProcess.ExitCode?.ToString() ?? "n/a")}</div>{heartbeat}";
        }

        if (task.LastDispatch is not null)
        {
            return $"<div>Dispatch: {Encode(task.LastDispatch.WorkerName)}{RenderDispatchModelSelection(task.LastDispatch)} &middot; {Encode(task.LastDispatch.Command)}</div>{RenderDispatchCostNote(task.LastDispatch)}";
        }

        if (task.LastExecution is not null)
        {
            return $"{execution}<pre>{Encode(OutputTextPreview.Create(task.LastExecution.Output).Text)}</pre>";
        }

        return "<span class=\"meta\">none</span>";
    }

    private static string[] ExtractWorkerResultSkills(TaskVerificationRecord verification)
    {
        var lines = $"{verification.StandardOutput}\n{verification.StandardError}"
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var start = Array.FindIndex(lines, line => line.Equals("WORKER_RESULT:", StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            return [];
        }

        var end = Array.FindIndex(lines, start + 1, line => line.Equals("END_WORKER_RESULT", StringComparison.OrdinalIgnoreCase));
        if (end < 0)
        {
            return [];
        }

        var skills = lines
            .Skip(start + 1)
            .Take(end - start - 1)
            .FirstOrDefault(line => line.StartsWith("skills:", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(skills))
        {
            return [];
        }

        return skills["skills:".Length..]
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(skill => !skill.Equals("none", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string RenderExecutionSummary(TaskExecutionRecord execution)
    {
        var inputTokens = execution.Usage?.InputTokens?.ToString() ?? "n/a";
        var outputTokens = execution.Usage?.OutputTokens?.ToString() ?? "n/a";
        var maxOutputTokens = execution.MaxOutputTokens?.ToString() ?? "n/a";
        var prompt = execution.PromptCharacterCount?.ToString() ?? "n/a";
        var capWarning = IsOutputTokenLimitHit(execution)
            ? " &middot; possible output cap hit"
            : string.Empty;

        return $"<div>Model: {Encode(execution.ProviderName)}/{Encode(execution.ModelName)} by {Encode(execution.AgentName)} <span class=\"meta\">tokens {inputTokens} in / {outputTokens} out &middot; prompt {prompt} chars &middot; max {maxOutputTokens} out &middot; stop reason {Encode(execution.StopReason)}{capWarning}</span></div>";
    }

    private static string RenderHeartbeatStatus(DispatchHeartbeatStatus heartbeat)
    {
        return $"<div class=\"meta\">{Encode(ProcessHeartbeatText.FormatInline(heartbeat))}</div>";
    }

    private static string RenderVerificationPlan(TaskSpec task)
    {
        return string.IsNullOrWhiteSpace(task.VerificationPlan)
            ? "<span class=\"meta\">none</span>"
            : Encode(OutputTextPreview.CreateSummary(task.VerificationPlan).Text);
    }

    private static string FormatTokenUsage(int? inputTokens, int? outputTokens)
    {
        return $"{inputTokens?.ToString() ?? "n/a"} in / {outputTokens?.ToString() ?? "n/a"} out";
    }

    private static string RenderModelUsageSummary(ModelUsageSummary usage)
    {
        var runs = usage.ExecutionCount == 1 ? "1 run" : $"{usage.ExecutionCount} runs";
        var complexity = usage.TaskComplexity is null ? string.Empty : $" ({Encode(usage.TaskComplexity.Value.ToString())})";
        var paid = usage.IsPotentiallyPaidProvider ? " [potentially paid]" : string.Empty;
        var limitHits = usage.OutputTokenLimitHitCount > 0
            ? $", cap hits {usage.OutputTokenLimitHitCount}{FormatMaxOutputTokens(usage.MaxOutputTokens)}"
            : string.Empty;
        var prompt = usage.PromptCharacterCount is null
            ? string.Empty
            : $", prompt {usage.PromptCharacterCount.Value} chars";
        return $"{Encode(usage.ProviderName)}/{Encode(usage.ModelName)}{complexity}{paid}: {runs}, {FormatTokenUsage(usage.InputTokens, usage.OutputTokens)}{limitHits}{prompt}";
    }

    private static string RenderDispatchModelSummary(DispatchModelSummary dispatch)
    {
        var dispatches = dispatch.DispatchCount == 1 ? "1 dispatch" : $"{dispatch.DispatchCount} dispatches";
        var complexity = dispatch.TaskComplexity is null ? string.Empty : $" ({Encode(dispatch.TaskComplexity.Value.ToString())})";
        var reasoning = string.IsNullOrWhiteSpace(dispatch.ReasoningEffort)
            ? string.Empty
            : $" reasoning {Encode(dispatch.ReasoningEffort)}";
        var paid = dispatch.IsPotentiallyPaidProvider ? " [potentially paid]" : string.Empty;
        var prompt = dispatch.PromptCharacterCount is null
            ? string.Empty
            : $", prompt {dispatch.PromptCharacterCount.Value} chars";
        return $"{Encode(dispatch.ProviderName)}/{Encode(dispatch.ModelName)}{complexity}{reasoning}{paid}: {dispatches}{prompt}";
    }

    private static string RenderModelFitSummary(ModelFitSummary fit)
    {
        var notes = fit.NoteCount == 1 ? "1 note" : $"{fit.NoteCount} notes";
        var shapes = FormatModelFitTaskShapes(fit.TaskShapes);
        return $"{Encode(fit.ProviderName)}/{Encode(fit.ModelName)}: {notes}; {string.Join(", ", BuildModelFitCounts(fit))}{shapes}";
    }

    private static List<string> BuildModelFitCounts(ModelFitSummary fit)
    {
        var counts = new List<string>();
        AddModelFitCount(counts, "adequate", fit.AdequateCount);
        AddModelFitCount(counts, "overkill", fit.OverkillCount);
        AddModelFitCount(counts, "underpowered", fit.UnderpoweredCount);
        AddModelFitCount(counts, "unknown", fit.UnknownCount);
        return counts.Count == 0 ? ["none"] : counts;
    }

    private static void AddModelFitCount(List<string> counts, string label, int count)
    {
        if (count > 0)
        {
            counts.Add($"{label} {count}");
        }
    }

    private static string FormatModelFitTaskShapes(IReadOnlyList<string>? taskShapes)
    {
        return taskShapes is { Count: > 0 }
            ? $"; shapes {string.Join(", ", taskShapes.Select(Encode))}"
            : string.Empty;
    }

    private static bool IsOutputTokenLimitHit(TaskExecutionRecord execution)
    {
        return OutputTokenLimit.IsHit(execution);
    }

    private static string FormatMaxOutputTokens(int? maxOutputTokens)
    {
        return maxOutputTokens is null ? string.Empty : $" of {maxOutputTokens}";
    }

    private static string RenderDispatchModelSelection(TaskDispatchRecord dispatch)
    {
        if (string.IsNullOrWhiteSpace(dispatch.ProviderName) || string.IsNullOrWhiteSpace(dispatch.ModelName))
        {
            return string.Empty;
        }

        var complexity = dispatch.TaskComplexity is null ? string.Empty : $" {Encode(dispatch.TaskComplexity.Value.ToString())}";
        var reasoning = string.IsNullOrWhiteSpace(dispatch.ReasoningEffort)
            ? string.Empty
            : $" reasoning {Encode(dispatch.ReasoningEffort)}";
        var prompt = dispatch.PromptCharacterCount is null
            ? string.Empty
            : $" prompt {dispatch.PromptCharacterCount.Value} chars";
        return $" <span class=\"meta\">{Encode(dispatch.ProviderName)}/{Encode(dispatch.ModelName)}{complexity}{reasoning}{prompt}</span>";
    }

    private static string RenderDispatchCostNote(TaskDispatchRecord dispatch)
    {
        return !string.IsNullOrWhiteSpace(dispatch.ProviderName) &&
            ProviderSmokeRunner.IsPaidProviderName(dispatch.ProviderName)
            ? $"<div class=\"attention\"><strong>Paid subscription handoff prepared</strong><br>Review subscription plan before start; {Encode(CostRecommendationText.LocalModelSwitchAction)} when the task is routine.</div>"
            : string.Empty;
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
            return $"<strong>Task {taskNumber}: {task.RequiredRole}</strong><br><span class=\"meta\">{Encode(Display(task.Status))}</span><br>{Encode(OutputTextPreview.CreateSummary(task.Description).Text)}";
        }

        var roleText = role?.ToString() ?? "Task";
        var statusText = status is null ? "unknown status" : Display(status.Value);
        var descriptionText = string.IsNullOrWhiteSpace(description) ? taskId.Value[..8] : description;
        return $"<strong>{Encode(roleText)}</strong><br><span class=\"meta\">{Encode(statusText)}</span><br>{Encode(OutputTextPreview.CreateSummary(descriptionText).Text)}";
    }

    private static string RenderNextActionControl(Goal goal, NextActionItem item, DashboardRenderOptions options)
    {
        var control = DashboardNextActionControls.Build(goal, item, options.HealthReport?.Agents, options.AgentDefinitions);
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

    private static string BuildSuggestedCommand(
        Goal goal,
        NextActionItem item,
        IReadOnlyList<AgentDefinition>? agents = null) =>
        ConsoleViews.BuildSuggestedCommand(goal, item, agents);

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
            StageReadinessStatus.ReadyToRun => $"run {taskNumber} | profile-dispatch {taskNumber} <profile-name>",
            StageReadinessStatus.InProgress when stage.LatestEvidence == TaskEvidenceKind.RunningProcess => $"refresh-dispatch {taskNumber}",
            StageReadinessStatus.InProgress when stage.LatestEvidence == TaskEvidenceKind.Dispatch => $"execute-dispatch {taskNumber} --confirm-dispatch-start",
            StageReadinessStatus.InProgress => $"task {taskNumber}",
            StageReadinessStatus.WaitingForHuman => "input-needed",
            StageReadinessStatus.NeedsVerification => $"verify {taskNumber} <command> | verify-manual {taskNumber} passed <note>",
            StageReadinessStatus.VerificationFailed => $"verifications {taskNumber} | retry {taskNumber} <note>",
            StageReadinessStatus.Verified => "stages",
            StageReadinessStatus.FailedOrCancelled => $"retry {taskNumber} <note>",
            _ => "monitor"
        };
    }

    private static string BuildStageSuggestedCommand(
        Goal goal,
        TaskStageReadiness stage,
        IReadOnlyList<AgentDefinition>? agents = null) =>
        ConsoleViews.BuildStageSuggestedCommand(goal, stage, agents);

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
        return TaskDisplayNumber.Resolve(goal, taskId);
    }

    private static string Encode(string value) => WebUtility.HtmlEncode(value);
}


