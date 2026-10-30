using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: fixed in-memory records; no files, shared state or processes.
public sealed class DispatchClassifierReceiptTests
{
    private static readonly DateTimeOffset CompletedAt = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BuildClassifierReceipt_PassedWorkerWithCommit_PreservesBaseline()
    {
        // Baseline ae479e84c: DispatchFailureClassifier.cs:1372-1434,1508-1516.
        // The original formatter counts 96 UTF-8 stdout bytes and reports worker provenance.
        const string stdout = "WORKER_RESULT:\nblockers: none\n" +
            "commit: 0123456789abcdef0123456789abcdef01234567\nEND_WORKER_RESULT";
        var verification = new TaskVerificationRecord(
            "opaque command", "C:\\repo", 0, stdout, string.Empty, CompletedAt,
            WorkerResultPresent: true, HasCommittedChanges: true);
        var task = new TaskSpec(TaskId.New(), "Implement feature", AgentRole.Developer);

        var receipt = DispatchClassifierReceipt.BuildClassifierReceipt(
            TaskOutcomeRules.CommittedWorkerResultEvidence, TaskOutcomeClass.Success,
            task, verification, workerResultPresent: true, hasCommittedChanges: true,
            DispatchOutcomeKind.VerifiedSuccess);

        Assert.Equal(
            "CLASSIFIER rule=committed-worker-result-evidence; outcome_class=success; " +
            "exit_code=0; root_exit_code=0; child_exit_code=unknown; exit_artifact=verification-record; " +
            "stdout_bytes=96; stderr_bytes=0; heartbeat_stdout_bytes=unknown; " +
            "duration_ms=unknown; worker_result=present(blockers=none); commit=worker; verdict=VerifiedSuccess",
            receipt);
    }

    [Fact]
    public void BuildClassifierReceipt_FailedWorkerWithBlockers_PreservesBaseline()
    {
        // Baseline ae479e84c: DispatchFailureClassifier.cs:1372-1434,1508-1516,1537-1546.
        // The original formatter counts 93/12 bytes and replaces the blocker semicolon.
        const string stdout = "WORKER_RESULT:\nblockers: exact-blocker - build failed; see log\n" +
            "commit: none\nEND_WORKER_RESULT";
        var verification = new TaskVerificationRecord(
            "opaque command", "C:\\repo", 1, stdout, "build failed", CompletedAt,
            WorkerResultPresent: true);
        var task = new TaskSpec(TaskId.New(), "Verify feature", AgentRole.Tester);

        var receipt = DispatchClassifierReceipt.BuildClassifierReceipt(
            TaskOutcomeRules.TesterWorkerResultBlocker, TaskOutcomeClass.RealFailure,
            task, verification, workerResultPresent: true, hasCommittedChanges: false,
            DispatchOutcomeKind.UnknownFailure);

        Assert.Equal(
            "CLASSIFIER rule=tester-worker-result-blocker; outcome_class=real-failure; " +
            "exit_code=1; root_exit_code=1; child_exit_code=unknown; exit_artifact=verification-record; " +
            "stdout_bytes=93; stderr_bytes=12; heartbeat_stdout_bytes=unknown; duration_ms=unknown; " +
            "worker_result=present(blockers=exact-blocker - build failed, see log); " +
            "commit=none; verdict=UnknownFailure",
            receipt);
    }

    [Fact]
    public void WithClassifierReceipt_NoWorkerResult_PreservesBaseline()
    {
        // Baseline ae479e84c: DispatchFailureClassifier.cs:1351-1370.
        var outcome = new DispatchOutcome(
            DispatchOutcomeKind.ProviderConnectivity, 1, false, null, null,
            RecoveryRecommendation.AutoRetry, "stream disconnected");

        var result = DispatchClassifierReceipt.WithClassifierReceipt(
            TaskOutcomeRules.ProviderConnectivity, outcome, 1);

        Assert.Equal(
            "CLASSIFIER rule=provider-connectivity; outcome_class=environmental; " +
            "exit_code=1; exit_artifact=direct-provider-failure; stdout_bytes=unknown; " +
            "stderr_bytes=unknown; heartbeat_stdout_bytes=unknown; worker_result=absent; " +
            "commit=none; verdict=ProviderConnectivity; evidence=stream disconnected",
            result.ClassifierReceipt);
        Assert.Equal(TaskOutcomeClass.Environmental, result.OutcomeClass);
    }

    [Fact]
    public void AppendEvidenceReceipt_UnsafeLongValue_PreservesBaseline()
    {
        // Baseline ae479e84c: DispatchFailureClassifier.cs:1362-1370,1537-1546.
        // Replacement and trim leave a 13-character prefix, then 227 x's at the 240 limit.
        var outcome = new DispatchOutcome(
            DispatchOutcomeKind.UnknownFailure, 1, false, null, null,
            RecoveryRecommendation.None, " \r\nalpha;\r\nbeta " + new string('x', 240) + " \r\n");

        var receipt = DispatchClassifierReceipt.AppendEvidenceReceipt("receipt", outcome);

        Assert.Equal("receipt; evidence=alpha,  beta " + new string('x', 227), receipt);
    }
}
