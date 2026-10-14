using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class BackgroundDispatchRunner
{
    private static TaskIntentSuppressionReceipt? TryReadIntentSuppression(
        GoalId goalId, TaskId taskId, TaskSpec task, TaskProcessRecord process, int exitCode,
        out bool suppressedPrimaryWithStartedSamples)
    {
        suppressedPrimaryWithStartedSamples = false;
        if (exitCode != TaskIntentAdmission.SuppressedExitCode ||
            !DispatchExitArtifacts.TryRead(process.ExitCodePath, out var artifact) ||
            artifact.Origin != DispatchExitArtifactOrigin.Native) return null;
        const string outputSuffix = ".out.log";
        if (!process.StandardOutputPath.EndsWith(outputSuffix, StringComparison.Ordinal)) return null;
        var parameters = DispatchProcessHost.ReadParameters(process.StandardOutputPath[..^outputSuffix.Length] + ".dispatch.json");
        if (parameters.TaskIntentAdmission is not { } admission || admission.GoalId != goalId.Value ||
            admission.TaskId != taskId.Value) return null;
        var store = new SqliteOperatorIntentStore(admission.StorePath, Path.GetDirectoryName(admission.StorePath)!, readOnly: true);
        var receipt = store.GetSuppression(process.StandardOutputPath);
        if (receipt is null || receipt.GoalId != goalId.Value || receipt.TaskId != taskId.Value ||
            receipt.PreparedVersion != admission.Version) return null;

        // A denied primary says nothing about samples whose gates were released first.
        // Only actual child admission records, not supervisor launch artifacts, prove start.
        var allSamplesDenied = true;
        foreach (var sample in PlannerSampleDispatcher.CreateArtifacts(process.StandardOutputPath,
                     task.RequiredRole == AgentRole.Planner ? task.LastDispatch?.PlannerSampleCount ?? 1 : 1))
        {
            if (store.HasLaunch(sample.StandardOutputPath))
            {
                suppressedPrimaryWithStartedSamples = true;
                allSamplesDenied = false;
                continue;
            }
            var denial = store.GetSuppression(sample.StandardOutputPath);
            if (denial is null || denial.GoalId != goalId.Value || denial.TaskId != taskId.Value ||
                denial.PreparedVersion != admission.Version)
                allSamplesDenied = false;
        }
        return allSamplesDenied ? receipt : null;
    }
}
