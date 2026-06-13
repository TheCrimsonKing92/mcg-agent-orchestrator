using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Rendering;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class ConsoleViews
{
public static void PrintAcceptanceEvidenceBundle(GoalAcceptanceEvidenceBundle bundle)
{
    Console.WriteLine();
    Console.WriteLine($"Acceptance evidence bundle: {(bundle.Passed ? "passed" : "blocked")}");
    Console.WriteLine($"Goal: {bundle.GoalId.Value[..8]} {bundle.Status}");
    Console.WriteLine($"Worktree: {bundle.WorktreePath ?? "(missing)"}");
    Console.WriteLine($"Changed files: {bundle.ChangedFiles.Count}");
    Console.WriteLine($"Diff stat: {OutputTextPreview.CreateTimeline(bundle.DiffStat).Text}");
    Console.WriteLine("Change classification: " +
        $"docsOnly={bundle.ChangeSummary.IsDocsOnly}, " +
        $"behavior={bundle.ChangeSummary.HasBehaviorChanges}, " +
        $"build={bundle.ChangeSummary.HasBuildSystemChanges}, " +
        $"security={bundle.ChangeSummary.HasSecuritySensitiveChanges}, " +
        $"generated={bundle.ChangeSummary.HasGeneratedArtifacts}, " +
        $"broad={bundle.ChangeSummary.RequiresBroadVerification}");
    Console.WriteLine($"Recommended verification: {bundle.ChangeSummary.RecommendedVerification}");
    Console.WriteLine($"Test impact: {bundle.TestImpactPlan.Summary}");
    foreach (var check in bundle.TestImpactPlan.Checks)
    {
        Console.WriteLine($"  {check.Name}: {check.CommandLine}");
        Console.WriteLine($"     reason: {OutputTextPreview.CreateTimeline(check.Reason).Text}");
    }

    Console.WriteLine("Verification policy: " +
        $"tests={(bundle.VerificationPolicy.RequiresTests ? "required" : "not required")}, " +
        $"humanReview={(bundle.VerificationPolicy.RequiresHumanReview ? "required" : "not required")}");
    foreach (var check in bundle.PolicyChecks)
    {
        Console.WriteLine($"  {check.State} - {(check.Required ? "required" : "optional")} {check.Name} [{check.Kind}] {check.CommandLine}");
        Console.WriteLine($"     reason: {OutputTextPreview.CreateTimeline(check.Reason).Text}");
    }

    foreach (var path in bundle.ChangedFiles.Take(8))
    {
        Console.WriteLine($"  {path}");
    }

    if (bundle.ChangedFiles.Count > 8)
    {
        Console.WriteLine($"  ... {bundle.ChangedFiles.Count - 8} more");
    }

    Console.WriteLine("Build environment:");
    Console.WriteLine($"  lease id: {bundle.BuildEnvironment.LeaseId}");
    Console.WriteLine($"  root: {(bundle.BuildEnvironment.RootExists ? "present" : "missing")} {bundle.BuildEnvironment.RootPath}");
    Console.WriteLine($"  lease: {(bundle.BuildEnvironment.LeaseMetadataExists ? "present" : "missing")} {bundle.BuildEnvironment.LeaseMetadataPath}");

    Console.WriteLine($"Acceptance checks: {bundle.AcceptanceChecks.Count}" +
        (bundle.VerificationSkipped ? " (verification skipped)" : ""));
    foreach (var check in bundle.AcceptanceChecks)
    {
        Console.WriteLine($"  {(check.Passed ? "passed" : "failed")} - {check.Name}" +
            (check.ExitCode is null ? "" : $" (exit {check.ExitCode})") +
            (string.IsNullOrWhiteSpace(check.ArtifactsPath) ? "" : $" artifacts={check.ArtifactsPath}"));
        if (!string.IsNullOrWhiteSpace(check.BrokerName))
        {
            Console.WriteLine($"    broker: {check.BrokerName}" +
                (string.IsNullOrWhiteSpace(check.LeaseId) ? "" : $" lease={check.LeaseId}") +
                (check.DurationMilliseconds is null ? "" : $" durationMs={check.DurationMilliseconds}") +
                (check.LockRemediationApplied ? " lockRemediation=True" : ""));
        }

        if (!string.IsNullOrWhiteSpace(check.ResultSummary))
        {
            Console.WriteLine($"    result: {OutputTextPreview.CreateTimeline(check.ResultSummary).Text}");
        }
    }

    Console.WriteLine($"Task verification records: {bundle.TaskEvidence.Count(item => item.VerificationExitCode == 0)}/{bundle.TaskEvidence.Count}");
    foreach (var task in bundle.TaskEvidence)
    {
        var command = OutputTextPreview.CreateTimeline(task.VerificationCommand).Text;
        var contract = task.HasWorkerResultContract ? "worker contract present" : "worker contract missing";
        Console.WriteLine($"  task {task.TaskNumber} {task.Role}: {task.Status}, verification exit {task.VerificationExitCode}, {contract}, {command}");
    }

    if (bundle.Blockers.Count == 0)
    {
        Console.WriteLine("Blockers: none");
    }
    else
    {
        Console.WriteLine("Blockers:");
        foreach (var blocker in bundle.Blockers)
        {
            Console.WriteLine($"  {blocker.Kind}: {OutputTextPreview.CreateTimeline(blocker.Message).Text}");
            Console.WriteLine($"     next: {OutputTextPreview.CreateTimeline(blocker.SuggestedCommand).Text}");
        }
    }

    Console.WriteLine();
}
}
