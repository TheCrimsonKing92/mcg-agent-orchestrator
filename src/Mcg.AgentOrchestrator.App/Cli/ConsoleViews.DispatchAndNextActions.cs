using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Dashboard.Rendering;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
public static void PrintProcessBatchResult(Goal goal, ProcessBatchExecutionResult result)
{
    Console.WriteLine();
    Console.WriteLine($"{result.Plan.Action}: ready={result.Plan.ReadyCount} skipped={result.Plan.SkippedCount} changed={result.Tasks.Count}");

    foreach (var task in result.Tasks)
    {
        var process = task.LastProcess;
        var status = process?.IsRunning is true
            ? $"running pid={process.ProcessId}"
            : $"exit={process?.ExitCode?.ToString() ?? "n/a"}";
        Console.WriteLine($"  Task {GetTaskDisplayNumber(goal, task.Id)} {status}");
    }

    var skipped = result.Plan.Items
        .Where(item => item.Status == ProcessBatchItemStatus.Skipped)
        .ToList();
    if (skipped.Count > 0)
    {
        Console.WriteLine("Skipped:");
        foreach (var item in skipped)
        {
            Console.WriteLine($"  Task {GetTaskDisplayNumber(goal, item.TaskId)}: {OutputTextPreview.CreateTimeline(item.Reason).Text}");
        }
    }

    Console.WriteLine();
}

public static void PrintSubscriptionStartResult(Goal goal, SubscriptionStartResult result)
{
    Console.WriteLine();
    foreach (var dispatchResult in result.Dispatches)
    {
        Console.WriteLine($"Task {GetTaskDisplayNumber(goal, dispatchResult.Task.Id)} profile {dispatchResult.Task.LastDispatch?.WorkerName}: {dispatchResult.PromptPath}");
    }

    Console.WriteLine($"Subscription dispatches created: {result.Dispatches.Count}");
    if (result.ParallelPlan.Decisions.Count > 0)
    {
        Console.WriteLine("Parallel plan:");
        foreach (var decision in result.ParallelPlan.Decisions)
        {
            var task = goal.Tasks.FirstOrDefault(candidate => candidate.Id.Value.Equals(decision.IntentId, StringComparison.OrdinalIgnoreCase));
            var taskLabel = task is null ? decision.IntentId : GetTaskDisplayNumber(goal, task.Id).ToString();
            var batch = decision.BatchNumber is null ? "n/a" : decision.BatchNumber.Value.ToString();
            Console.WriteLine($"  Task {taskLabel}: {decision.Disposition} batch={batch} - {OutputTextPreview.CreateTimeline(string.Join("; ", decision.Reasons)).Text}");
        }
    }

    PrintProcessBatchResult(goal, result.Processes);
}

public static void PrintNextActions(
    Goal goal,
    GoalNextActions actions,
    WorkerProfileCatalog workerProfiles,
    IReadOnlyList<AgentDefinition>? agents = null,
    GoalHealthReport? health = null,
    GoalOperatorDisposition? conductorDisposition = null)
{
    Console.WriteLine();
    Console.WriteLine($"Goal {actions.GoalId.Value[..8]} {actions.Status}: {OutputTextPreview.CreateSummary(actions.Objective).Text}");
    if (health is not null)
    {
        Console.WriteLine($"Health: {health.Disposition} score={health.Score}; recommendation: {health.Recommendation}; command: {health.SuggestedCommand}");
    }

    var verificationSatisfied = goal.Tasks.Count > 0 && goal.Tasks.All(task => task.LastVerification?.Succeeded == true);
    var operatorDisposition = conductorDisposition ?? new GoalOperatorDispositionSurface().Evaluate(
        goal,
        pendingHumanInputCount: 0,
        verificationSatisfied);
    PrintOperatorDisposition(operatorDisposition);

    Console.WriteLine("Next actions:");

    for (var index = 0; index < actions.Items.Count; index++)
    {
        var item = actions.Items[index];
        Console.WriteLine($"  {index + 1}. {item.Kind}: {OutputTextPreview.CreateTimeline(item.Message).Text}");
        if (DispatchRecoveryView.EvaluateState(goal, item) is { } dispatchState)
        {
            PrintDispatchState(dispatchState, "     ");
        }

        Console.WriteLine($"     command: {BuildSuggestedCommand(goal, item, agents)}");
        var control = DashboardNextActionControls.Build(goal, item, workerProfiles, agentDefinitions: agents);
        if (!string.IsNullOrWhiteSpace(control?.CostRisk))
        {
            var recommendation = string.IsNullOrWhiteSpace(control.CostRecommendation)
                ? string.Empty
                : $" {control.CostRecommendation}";
            Console.WriteLine($"     cost: {control.CostRisk}.{recommendation}");
        }
    }

    var runCommand = health?.SuggestedCommand is { Length: > 0 } hCmd
        ? hCmd
        : actions.Items.Count > 0 ? BuildSuggestedCommand(goal, actions.Items[0], agents) : null;
    if (runCommand is not null)
    {
        Console.WriteLine($"Run: {runCommand}");
    }

    Console.WriteLine();
}

public static void PrintOperatorDisposition(GoalOperatorDisposition disposition)
{
    var blockers = disposition.Blockers.Count == 0
        ? "none"
        : string.Join(",", disposition.Blockers);
    Console.WriteLine($"Disposition: state='{disposition.State}' confidence='{disposition.Confidence}' action='{disposition.NextSafeCommand}' blockers='{blockers}' reason='{OutputTextPreview.CreateTimeline(disposition.Reason).Text}' fresh='{disposition.FreshAt:u}'");
}

public static void PrintDispatchState(DispatchAuthoritativeState dispatchState, string indent = "")
{
    var decision = dispatchState.RecoveryDecision;
    var blocker = string.IsNullOrWhiteSpace(decision.Blocker)
        ? string.Empty
        : $" blocker='{decision.Blocker}'";
    Console.WriteLine($"{indent}dispatch-state: state='{dispatchState.Kind}' action='{dispatchState.RecommendedAction}' live={dispatchState.ProcessTree.HasLiveProcess} child_pid={dispatchState.ProcessTree.ChildProcessId?.ToString() ?? "none"} exit_artifact={dispatchState.Artifacts.ExitCodeExists} dirty_worktree={dispatchState.Worktree.IsDirty?.ToString() ?? "unknown"}");
    Console.WriteLine($"{indent}recovery: action='{decision.ActionName}' evidence='{decision.EvidencePath}' reason='{decision.Reason}'{blocker}");
}
}


