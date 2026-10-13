using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class BackgroundDispatchRunner
{
    private static TaskIntentSuppressionReceipt? TryReadIntentSuppression(
        GoalId goalId, TaskId taskId, TaskProcessRecord process, int exitCode)
    {
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
        return receipt is not null && receipt.GoalId == goalId.Value && receipt.TaskId == taskId.Value &&
            receipt.PreparedVersion == admission.Version ? receipt : null;
    }
}
