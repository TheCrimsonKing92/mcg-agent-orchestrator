using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Rendering;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
public static void PrintGoalReadinessPreflight(GoalReadinessPreflightReport report)
{
    Console.WriteLine();
    Console.WriteLine($"Goal readiness {report.GoalId.Value[..8]} {report.Status}: {OutputTextPreview.CreateSummary(report.Objective).Text}");
    Console.WriteLine($"Recommendation: {report.Recommendation}");
    Console.WriteLine($"Start allowed: {report.AllowsUnattendedStart}");
    Console.WriteLine($"Requires confirmation: {report.RequiresOperatorConfirmation}");
    Console.WriteLine($"Workspace: required={report.RequiresWorkspace}, present={report.HasWorkspace}");
    Console.WriteLine($"File scope confidence: {report.FileScopeConfidence}");
    Console.WriteLine("Findings:");
    foreach (var finding in report.Findings)
    {
        Console.WriteLine($"  {finding.Severity} {finding.Kind}: {OutputTextPreview.CreateTimeline(finding.Message).Text}");
    }

    Console.WriteLine("Tasks:");
    foreach (var task in report.Tasks)
    {
        var scopes = task.FileScopes.Count == 0 ? "none" : string.Join(", ", task.FileScopes.Take(4));
        if (task.FileScopes.Count > 4)
        {
            scopes += $", ... {task.FileScopes.Count - 4} more";
        }

        Console.WriteLine($"  task {task.TaskNumber} {task.Role}: {task.Status}, complexity={task.Complexity}, agent={(task.HasAssignedAgent ? "assigned" : "missing")}, scopes={scopes}");
    }

    Console.WriteLine();
}
}
