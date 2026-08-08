using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ChaosGateDispatchNoChangeTests : ChaosGateTestBase
{
    // Gate 1: False-positive completion rejection
    [Xunit.Fact(DisplayName = "ChaosGate1_worker_exits_0_with_no_file_change_is_rejected")]
    public void Gate1_WorkerExitsZeroWithNoFileChange_IsRejected()
    {
        var root = CreateSeededRepo();
        var (kernel, goal, task, process) = CreateChaosDispatch(
            root, AgentRole.Developer,
            WorkerResultBlock("none", "dotnet build", "not run"),
            string.Empty,
            mutateWorktree: null);

        new BackgroundDispatchRunner(isStillRunning: _ => false)
            .RefreshLatestProcess(kernel, goal.Id, task.Id);

        Assert.Equal(WorkTaskStatus.Failed, task.Status);
        Assert.Equal(1, task.LastVerification!.ExitCode);
        Assert.Contains(
            "did not produce required relevant file-change evidence",
            task.LastVerification.StandardError,
            StringComparison.Ordinal);
        Assert.Contains(
            DispatchFailureDiagnosticMarker.Format(DispatchFailureDiagnosticMarker.RequiredFileChangeEvidenceMissing),
            task.LastVerification.StandardError,
            StringComparison.Ordinal);
        Assert.Equal("0", File.ReadAllText(process.ExitCodePath).Trim());

        var outcome = DispatchFailureClassifier.Classify(task, task.LastVerification);
        Assert.Equal(DispatchOutcomeKind.UnknownFailure, outcome.Kind);
        Assert.Equal(RecoveryRecommendation.OperatorNeeded, outcome.RecoveryRecommendation);
        Assert.Equal(TaskOutcomeClass.UnknownEra, outcome.OutcomeClass);
        Assert.Contains("rule=required-file-change-evidence-missing", outcome.ClassifierReceipt, StringComparison.Ordinal);
    }
}
