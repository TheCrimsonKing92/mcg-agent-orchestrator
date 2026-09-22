using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.App.Providers;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
public static void PrintDelegationPlan(Goal goal, DelegationPlan plan)
{
    if (plan.Assignments.Count == 0)
    {
        Console.WriteLine("No pending tasks were delegated.");
        return;
    }

    Console.WriteLine("Delegated tasks:");
    foreach (var assignment in plan.Assignments)
    {
        Console.WriteLine($"  task {GetTaskDisplayNumber(goal, assignment.TaskId)} -> {assignment.AgentId.Value} ({assignment.Role})");
    }
}

public static void PrintTask(Goal goal, TaskSpec task)
{
    Console.WriteLine($"{task.Id.Value[..8]} [{task.Status}] {task.RequiredRole}: {OutputTextPreview.CreateSummary(task.Description).Text}");
    Console.WriteLine($"Assigned agent: {(task.AssignedAgentId is null ? "unassigned" : task.AssignedAgentId.Value)}");
    Console.WriteLine($"Verification plan: {(task.VerificationPlan is null ? "none" : OutputTextPreview.CreateSummary(task.VerificationPlan).Text)}");

    if (task.LastExecution is not null)
    {
        Console.WriteLine($"Last execution: {task.LastExecution.ProviderName}/{task.LastExecution.ModelName} by {task.LastExecution.AgentName}");
        Console.WriteLine($"Stop reason: {task.LastExecution.StopReason}");
        Console.WriteLine($"Usage: input={task.LastExecution.Usage?.InputTokens?.ToString() ?? "n/a"} output={task.LastExecution.Usage?.OutputTokens?.ToString() ?? "n/a"} maxOutput={task.LastExecution.MaxOutputTokens?.ToString() ?? "n/a"}");
        if (task.LastExecution.PromptCharacterCount is { } promptCharacterCount)
        {
            Console.WriteLine($"Prompt size: {promptCharacterCount} chars");
        }
        if (IsOutputTokenLimitHit(task.LastExecution))
        {
            Console.WriteLine("Model note: possible output token cap hit.");
        }
        Console.WriteLine("Output:");
        Console.WriteLine(OutputTextPreview.Create(task.LastExecution.Output).Text.TrimEnd());
    }

    if (task.LastDispatch is not null)
    {
        Console.WriteLine($"Last dispatch: worker={task.LastDispatch.WorkerName} command={task.LastDispatch.Command}");
        if (!string.IsNullOrWhiteSpace(task.LastDispatch.ProviderName) && !string.IsNullOrWhiteSpace(task.LastDispatch.ModelName))
        {
            Console.WriteLine($"Dispatch model: {task.LastDispatch.ProviderName}/{task.LastDispatch.ModelName} complexity={task.LastDispatch.TaskComplexity?.ToString() ?? "unknown"} reasoning={task.LastDispatch.ReasoningEffort ?? "default"}");
            if (ProviderSmokeRunner.IsPaidProviderName(task.LastDispatch.ProviderName))
            {
                Console.WriteLine($"Cost note: paid subscription handoff prepared; review subscription-plan before start; {CostRecommendationText.LocalModelSwitchAction} when the task is routine.");
            }
        }
        if (task.LastDispatch.PromptCharacterCount is { } promptCharacterCount)
        {
            Console.WriteLine($"Prompt size: {promptCharacterCount} chars");
        }
        Console.WriteLine($"Working directory: {task.LastDispatch.WorkingDirectory}");
        Console.WriteLine($"Dispatched: {task.LastDispatch.DispatchedAt:u}");
    }

    if (task.LastProcess is not null)
    {
        Console.WriteLine($"Last process: pid={task.LastProcess.ProcessId} running={task.LastProcess.IsRunning}");
        Console.WriteLine($"Started: {task.LastProcess.StartedAt:u}");
        if (task.LastProcess.WasCancelled)
        {
            Console.WriteLine("Cancelled: yes");
        }
        if (task.LastProcess.CompletedAt is not null)
        {
            Console.WriteLine($"Completed: {task.LastProcess.CompletedAt:u} exit={task.LastProcess.ExitCode?.ToString() ?? "n/a"}");
        }
        Console.WriteLine($"stdout path: {task.LastProcess.StandardOutputPath}");
        Console.WriteLine($"stderr path: {task.LastProcess.StandardErrorPath}");
        foreach (var line in ProcessHeartbeatText.FormatLines(ProcessLogReader.ReadHeartbeat(task.LastProcess)))
        {
            Console.WriteLine(line);
        }
    }

    if (task.LastVerification is not null)
    {
        Console.WriteLine($"Last verification: exit={task.LastVerification.ExitCode} command={task.LastVerification.Command}");
        Console.WriteLine($"Completed: {task.LastVerification.CompletedAt:u}");
        Console.WriteLine($"Verification history: {task.VerificationHistory.Count}");

        if (!string.IsNullOrWhiteSpace(task.LastVerification.StandardOutput))
        {
            Console.WriteLine("stdout:");
            Console.WriteLine(OutputTextPreview.CreateVerificationLog(task.LastVerification.StandardOutput, task.LastVerification.StandardOutputPath).Text.TrimEnd());
        }

        if (!string.IsNullOrWhiteSpace(task.LastVerification.StandardError))
        {
            Console.WriteLine("stderr:");
            Console.WriteLine(OutputTextPreview.CreateVerificationLog(task.LastVerification.StandardError, task.LastVerification.StandardErrorPath).Text.TrimEnd());
        }

        if (DispatchFailureClassifier.TryBuildDirtyDispatchRecovery(task, out var recovery))
        {
            PrintDirtyDispatchRecovery(GetTaskDisplayNumber(goal, task.Id), recovery);
        }
    }

    Console.WriteLine("Task timeline:");
    PrintTaskTimeline(goal, task);
}

public static void PrintRefreshDispatchResult(
    Goal goal,
    TaskSpec task,
    CliArgumentParser.RefreshDispatchOptions options)
{
    var disposition = new GoalOperatorDispositionSurface().EvaluateTask(goal, task);
    var state = disposition.DispatchState;
    var dispatch = task.LastDispatch;
    var process = task.LastProcess;
    var verification = task.LastVerification;

    Console.WriteLine($"Task: id={task.Id.Value[..8]} status={task.Status} role={task.RequiredRole} assigned={OneLine(task.AssignedAgentId?.Value, "unassigned")}");
    Console.WriteLine(dispatch is null
        ? "Dispatch: none"
        : $"Dispatch: worker={OneLine(dispatch.WorkerName)} provider={OneLine(dispatch.ProviderName, "unknown")} model={OneLine(dispatch.ModelName, "unknown")} lane={OneLine(dispatch.DispatchLane, "unknown")} session={OneLine(dispatch.ProviderSessionId, "none")} dispatched={dispatch.DispatchedAt:u}");

    Console.WriteLine(process is null
        ? "Process: none"
        : $"Process: wrapper_pid={process.ProcessId} wrapper_alive={state?.ProcessTree.Processes.FirstOrDefault(item => item.ProcessId == process.ProcessId)?.IsAlive.ToString().ToLowerInvariant() ?? "unknown"} child_pid={state?.ProcessTree.ChildProcessId?.ToString() ?? process.ChildProcessId?.ToString() ?? "none"} child_alive={state?.ProcessTree.HasLiveChild.ToString().ToLowerInvariant() ?? "unknown"} running={process.IsRunning.ToString().ToLowerInvariant()} completed={process.CompletedAt?.ToString("u") ?? "none"}");

    Console.WriteLine(state is null
        ? "Dispatch state: none"
        : $"Dispatch state: kind={state.Kind} recommended={OneLine(state.RecommendedAction)} summary={OneLine(state.Summary)}");
    Console.WriteLine(state is null
        ? "Heartbeat: none"
        : $"Heartbeat: {OneLine(ProcessHeartbeatText.FormatInline(state.Heartbeat))}");
    Console.WriteLine(process is null
        ? "Exit evidence: none"
        : $"Exit evidence: exit={process.ExitCode?.ToString() ?? "unknown"} origin={state?.Artifacts.ExitArtifactOrigin.ToString() ?? process.ExitArtifactOrigin.ToString()} reason={OneLine(state?.Artifacts.ExitArtifactReason ?? process.ExitArtifactReason, "none")} artifact={(state?.Artifacts.ExitCodeExists == true ? "present" : "missing")} path={OneLine(process.ExitCodePath, "none")}");

    if (verification is null)
    {
        Console.WriteLine("Exit classification: none");
        Console.WriteLine("Structured blockers: status=unknown value=none");
        Console.WriteLine("Latest verification: none");
    }
    else
    {
        var outcome = DispatchFailureClassifier.Classify(task, verification);
        var hasBlockerStatus = WorkerResultBlockers.TryGetBlockersStatus(verification, out var blockerStatus);
        var hasBlocker = WorkerResultBlockers.TryFindBlocker(verification, out var blocker);
        Console.WriteLine($"Exit classification: kind={outcome.Kind} class={outcome.OutcomeClass} recovery={outcome.RecoveryRecommendation} evidence={OneLine(outcome.EvidenceSummary, "none")}");
        Console.WriteLine($"Structured blockers: status={(hasBlockerStatus ? blockerStatus.ToString().ToLowerInvariant() : "unknown")} value={OneLine(hasBlocker ? blocker : null, "none")}");
        Console.WriteLine($"Latest verification: exit={verification.ExitCode} completed={verification.CompletedAt:u} worker_result={verification.WorkerResultPresent.ToString().ToLowerInvariant()} command={OneLine(verification.Command)} stdout_path={OneLine(verification.StandardOutputPath, "none")} stderr_path={OneLine(verification.StandardErrorPath, "none")}");
    }

    Console.WriteLine($"Disposition: state={disposition.State} confidence={disposition.Confidence} reason={OneLine(disposition.Reason)}");
    Console.WriteLine($"Next action: {OneLine(disposition.NextSafeCommand)}");

    if (options.IncludeHistory)
    {
        Console.WriteLine("Task timeline:");
        PrintTaskTimeline(goal, task, options.HistoryLimit);
    }
}

public static void PrintTimeline(Goal goal)
{
    Console.WriteLine("Timeline:");
    foreach (var item in goal.Timeline.OrderBy(evt => evt.OccurredAt))
    {
        var task = item.TaskId is null ? "goal" : item.TaskId.Value[..8];
        Console.WriteLine($"  {item.OccurredAt:u} {item.Kind} {task}: {OutputTextPreview.CreateTimeline(item.Message).Text}");
    }
}

public static void PrintTaskTimeline(Goal goal, TaskSpec task, int? limit = null)
{
    var events = goal.Timeline.Where(evt => evt.TaskId == task.Id).OrderBy(evt => evt.OccurredAt).ToList();
    if (limit is { } count && events.Count > count)
    {
        events = events.TakeLast(count).ToList();
    }
    if (events.Count == 0)
    {
        Console.WriteLine("  no task timeline events");
        return;
    }

    foreach (var item in events)
    {
        Console.WriteLine($"  {item.OccurredAt:u} {item.Kind}: {OneLine(OutputTextPreview.CreateTimeline(item.Message).Text)}");
    }
}

private static string OneLine(string? value, string fallback = "unknown")
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return fallback;
    }

    const int maxLength = 400;
    var singleLine = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    return singleLine.Length <= maxLength ? singleLine : singleLine[..(maxLength - 14)] + "... [truncated]";
}

public static void PrintPendingHumanInput(AgentOrchestratorKernel kernel)
{
    var pending = kernel.HumanInputRequests.Where(request => !request.IsCompleted).OrderBy(request => request.RequestedAt).ToList();
    if (pending.Count == 0)
    {
        Console.WriteLine("No pending human input.");
        return;
    }

    foreach (var request in pending)
    {
        var task = request.TaskId is null ? "goal" : request.TaskId.Value[..8];
        var counts = kernel.GetHumanInputRequestCounts(request.GoalId, request.TaskId);
        Console.WriteLine(
            $"{request.Id.Value[..8]} goal={request.GoalId.Value[..8]} task={task} " +
            $"requests={counts.Open} open / {counts.Total} total: {OutputTextPreview.CreateSummary(request.Question).Text}");
    }
}

private static bool IsOutputTokenLimitHit(TaskExecutionRecord execution)
{
    return OutputTokenLimit.IsHit(execution);
}

}


