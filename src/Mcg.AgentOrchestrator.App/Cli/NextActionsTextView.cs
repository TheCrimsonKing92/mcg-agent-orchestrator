using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class NextActionsTextView
{
public static void Print(
    Goal goal,
    GoalNextActions actions,
    WorkerProfileCatalog workerProfiles,
    IReadOnlyList<AgentDefinition>? agents = null,
    GoalHealthReport? health = null,
    GoalOperatorDisposition? conductorDisposition = null,
    Func<ProcessCommandLineSnapshot>? processSnapshotFactory = null,
    IClock? diagnosticsClock = null)
{
    var processInspection = new ProcessInspectionSnapshotScope(
        processSnapshotFactory ?? ProcessCommandLines.SnapshotOperation);
    Console.WriteLine();
    Console.WriteLine($"Goal {actions.GoalId.Value[..8]} {actions.Status}: {OutputTextPreview.CreateSummary(actions.Objective).Text}");
    if (health is not null)
    {
        Console.WriteLine($"Health: {health.Disposition} score={health.Score}; recommendation: {health.Recommendation}; command: {health.SuggestedCommand}");
    }

    var verificationSatisfied = goal.Tasks.Count > 0 && goal.Tasks.All(task => task.LastVerification?.Succeeded == true);
    var operatorDisposition = conductorDisposition ?? new GoalOperatorDispositionSurface(diagnosticsClock).Evaluate(
        goal,
        pendingHumanInputCount: 0,
        verificationSatisfied,
        commandLineSnapshot: processInspection.Get());
    PrintOperatorDisposition(operatorDisposition);

    Console.WriteLine("Next actions:");

    for (var index = 0; index < actions.Items.Count; index++)
    {
        var item = actions.Items[index];
        Console.WriteLine($"  {index + 1}. {item.Kind}: {OutputTextPreview.CreateTimeline(item.Message).Text}");
        if (DispatchRecoveryView.EvaluateState(goal, item, processInspection.Get()) is { } dispatchState)
        {
            DispatchResultTextView.PrintDispatchState(dispatchState, "     ");
        }

        Console.WriteLine($"     command: {NextActionCommandAdvice.BuildSuggestedCommand(goal, item, agents)}");
        var control = NextActionControls.Build(goal, item, workerProfiles, agentDefinitions: agents);
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
        : actions.Items.Count > 0 ? NextActionCommandAdvice.BuildSuggestedCommand(goal, actions.Items[0], agents) : null;
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

}
