using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Providers;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
public static void PrintAdvanceResult(AdvanceResultDto result)
{
    Console.WriteLine();
    Console.WriteLine(result.Executed
        ? $"Advanced goal {result.GoalId[..8]}: {OutputTextPreview.CreateTimeline(result.Message).Text}"
        : $"Cannot advance goal {result.GoalId[..8]}: {OutputTextPreview.CreateTimeline(result.Message).Text}");

    if (result.Action is not null)
    {
        Console.WriteLine($"Next action: {result.Action.Kind} - {OutputTextPreview.CreateTimeline(result.Action.Message).Text}");
        Console.WriteLine($"Command: {result.Action.SuggestedCommand}");
    }

    Console.WriteLine();
}

public static string BuildSuggestedCommand(NextActionItem item, int? taskNumber)
{
    return item.Kind switch
    {
        NextActionKind.AnswerHumanInput => item.HumanInputRequestId is null
            ? "pending"
            : $"answer {item.HumanInputRequestId.Value[..8]} <answer>",
        NextActionKind.InspectFailedTask => taskNumber is null ? "monitor" : $"task {taskNumber} | retry {taskNumber} <note>",
        NextActionKind.FixFailedVerification => taskNumber is null ? "monitor" : $"verifications {taskNumber} | retry {taskNumber} <note>",
        NextActionKind.RefreshRunningProcess => taskNumber is null ? "monitor" : $"refresh-dispatch {taskNumber}",
        NextActionKind.ExecuteRecordedDispatch => taskNumber is null ? "monitor" : $"execute-dispatch {taskNumber} --confirm-dispatch-start",
        NextActionKind.VerifyCompletedTask => taskNumber is null ? "monitor" : $"verify {taskNumber} <command> | verify-manual {taskNumber} passed <note>",
        NextActionKind.RunAssignedTask => taskNumber is null ? "monitor" : $"run {taskNumber}",
        NextActionKind.DelegatePendingTask => "delegate",
        NextActionKind.MonitorGoal => "monitor",
        _ => "monitor"
    };
}

public static string BuildSuggestedCommand(
    Goal goal,
    NextActionItem item,
    IReadOnlyList<AgentDefinition>? agents = null)
{
    int? taskNumber = null;
    if (item.TaskId is not null)
    {
        taskNumber = GetTaskDisplayNumber(goal, item.TaskId);
    }

    var command = BuildSuggestedCommand(item, taskNumber);
    if (item.TaskId is null || taskNumber is null)
    {
        return command;
    }

    var task = goal.Tasks.FirstOrDefault(candidate => candidate.Id == item.TaskId);
    if (task is null)
    {
        return command;
    }

    return item.Kind switch
    {
        NextActionKind.ExecuteRecordedDispatch =>
            SubscriptionPromptCostGuard.EvaluatePreparedDispatchStart(goal, task) is not null
                ? $"{command} {SubscriptionPromptCostGuard.CliConfirmationFlag}"
                : command,
        NextActionKind.FixFailedVerification when DispatchFailureClassifier.TryBuildDirtyDispatchRecovery(task, out var recovery) =>
            BuildDirtyDispatchRecoverySuggestedCommand(taskNumber.Value, recovery),
        NextActionKind.RunAssignedTask => BuildRunAssignedTaskCommand(goal, task, taskNumber.Value, agents),
        _ => command
    };
}

private static string BuildRunAssignedTaskCommand(
    Goal goal,
    TaskSpec task,
    int taskNumber,
    IReadOnlyList<AgentDefinition>? agents)
{
    var agent = ResolveAssignedAgent(task, agents);
    if (agent is null)
    {
        return $"run {taskNumber}";
    }

    if (AgentExecutionPolicies.AllowsSubscription(agent.ExecutionPolicy))
    {
        return $"subscription-dispatch {taskNumber}";
    }

    var command = $"run {taskNumber}";
    var preview = AgentTaskRunner.PreviewRun(goal, task, [agent]);
    if (!ProviderSmokeRunner.IsPaidProviderName(preview.ProviderName))
    {
        return command;
    }

    command += " --confirm-paid-api-run";
    return ApiPromptCostGuard.Evaluate(preview, goal) is null
        ? command
        : $"{command} {ApiPromptCostGuard.CliConfirmationFlag}";
}

public static void PrintRunGoalResult(Goal goal, RunGoalService.RunGoalResult result)
{
    Console.WriteLine();
    var goalPrefix = goal.Id.Value[..8];
    if (result.Executed)
    {
        Console.WriteLine($"Goal {goalPrefix}: {result.CompletedTasks.Count} task(s) transitioned. {OutputTextPreview.CreateTimeline(result.StopReason).Text}");
    }
    else
    {
        Console.WriteLine($"Goal {goalPrefix}: no steps executed. {OutputTextPreview.CreateTimeline(result.StopReason).Text}");
    }

    foreach (var task in result.CompletedTasks)
    {
        var status = task.Succeeded ? "succeeded" : "failed";
        Console.WriteLine($"  Task {task.TaskNumber} {task.TaskId[..8]}: {status} - {OutputTextPreview.CreateTimeline(task.Description).Text}");
        if (!string.IsNullOrWhiteSpace(task.OutputTail))
        {
            Console.WriteLine($"    tail: {OutputTextPreview.CreateTimeline(task.OutputTail).Text}");
        }
    }

    if (result.BlockingAction is not null)
    {
        Console.WriteLine($"Blocked: {result.BlockingAction.Kind} - {OutputTextPreview.CreateTimeline(result.BlockingAction.Message).Text}");
        Console.WriteLine($"Command: {result.BlockingAction.SuggestedCommand}");
    }

    if (result.StopEvidence is not null)
    {
        var task = result.StopEvidence.TaskNumber is null
            ? "goal"
            : $"task {result.StopEvidence.TaskNumber} {result.StopEvidence.TaskId![..8]}";
        Console.WriteLine($"Stop evidence: {task} - {OutputTextPreview.CreateTimeline(result.StopEvidence.Reason).Text}");
        if (!string.IsNullOrWhiteSpace(result.StopEvidence.OutputTail))
        {
            Console.WriteLine($"Output tail: {OutputTextPreview.CreateVerificationLog(result.StopEvidence.OutputTail).Text}");
        }
    }

    if (result.ContinueAfter.HasValue)
    {
        Console.WriteLine($"Retry after: {result.ContinueAfter:u}");
    }

    Console.WriteLine();
}

private static AgentDefinition? ResolveAssignedAgent(TaskSpec task, IReadOnlyList<AgentDefinition>? agents)
{
    if (agents is null)
    {
        return null;
    }

    return task.AssignedAgentId is null
        ? agents.FirstOrDefault(agent => agent.Status == AgentStatus.Available && agent.Role == task.RequiredRole)
        : agents.FirstOrDefault(agent => agent.Id == task.AssignedAgentId);
}
}


