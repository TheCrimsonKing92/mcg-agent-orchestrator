using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Rendering;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
public static void PrintGoalRecoveryReport(GoalRecoveryReport report)
{
    Console.WriteLine();
    Console.WriteLine($"Goal recovery {report.GoalId.Value[..8]} {report.Status}: {OutputTextPreview.CreateSummary(report.Objective).Text}");
    Console.WriteLine(report.WorktreeExists
        ? $"Worktree: {report.WorktreePath} dirty={report.WorktreeDirty?.ToString() ?? "unknown"} diff={report.HasBranchDiff}"
        : "Worktree: missing");
    Console.WriteLine("Build lease: " +
        $"{report.BuildLease.LeaseId} " +
        $"root={(report.BuildLease.RootExists ? "present" : "missing")} " +
        $"artifacts={(report.BuildLease.ArtifactsPathExists ? "present" : "missing")} " +
        $"metadata={(report.BuildLease.LeaseMetadataExists ? "present" : "missing")} " +
        $"ownerPid={report.BuildLease.OwnerProcessId?.ToString() ?? "unknown"} " +
        $"ownerAlive={report.BuildLease.OwnerProcessAlive} " +
        $"canCleanup={report.BuildLease.CanCleanup}");
    Console.WriteLine($"  {OutputTextPreview.CreateTimeline(report.BuildLease.Detail).Text}");
    if (report.ChangeSummary.Files.Count > 0)
    {
        Console.WriteLine("Change classification: " +
            $"files={report.ChangeSummary.Files.Count}, " +
            $"docsOnly={report.ChangeSummary.IsDocsOnly}, " +
            $"behavior={report.ChangeSummary.HasBehaviorChanges}, " +
            $"build={report.ChangeSummary.HasBuildSystemChanges}, " +
            $"security={report.ChangeSummary.HasSecuritySensitiveChanges}, " +
            $"generated={report.ChangeSummary.HasGeneratedArtifacts}, " +
            $"broad={report.ChangeSummary.RequiresBroadVerification}");
        Console.WriteLine($"Recommended verification: {report.ChangeSummary.RecommendedVerification}");
        Console.WriteLine($"Test impact: {report.TestImpactPlan.Summary}");
        foreach (var check in report.TestImpactPlan.Checks)
        {
            Console.WriteLine($"  {check.Name}: {check.CommandLine}");
        }
    }
    if (report.OperationJournal.HasEntries)
    {
        Console.WriteLine($"Operation journal: {report.OperationJournal.Path}");
        foreach (var entry in report.OperationJournal.LatestByOperation.TakeLast(6))
        {
            Console.WriteLine($"  {entry.Operation}: {entry.Status} at {entry.At:u}" +
                (string.IsNullOrWhiteSpace(entry.Detail) ? "" : $" - {OutputTextPreview.CreateTimeline(entry.Detail).Text}"));
        }

        if (report.OperationJournal.InterruptedOperations.Count > 0)
        {
            Console.WriteLine("Interrupted operations:");
            foreach (var entry in report.OperationJournal.InterruptedOperations)
            {
                Console.WriteLine($"  {entry.Operation}: idempotency={entry.IdempotencyKey}");
            }
        }
    }
    Console.WriteLine($"Pending human input: {report.PendingHumanInputCount}");

    Console.WriteLine("Findings:");
    if (report.TaskFindings.Count == 0)
    {
        Console.WriteLine("  none");
    }
    else
    {
        foreach (var finding in report.TaskFindings)
        {
            Console.WriteLine($"  Task {finding.TaskNumber} {finding.Role} {finding.Status}: {OutputTextPreview.CreateTimeline(finding.Finding).Text}");
            if (finding.RecoveryDecision is { } decision)
            {
                Console.WriteLine($"     recovery: action='{decision.ActionName}' evidence='{decision.EvidencePath}'");
            }
            Console.WriteLine($"     command: {finding.SuggestedCommand}");
        }
    }

    Console.WriteLine("Recommended actions:");
    foreach (var action in report.RecommendedActions)
    {
        Console.WriteLine($"  - {action}");
    }

    Console.WriteLine();
}
}
