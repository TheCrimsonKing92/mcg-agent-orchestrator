using Mcg.AgentOrchestrator.Core;

public sealed class WorkerResultBlockersTests
{
    [Xunit.Fact(DisplayName = "TryFindEvidenceRequest_reads_latest_worker_result_field")]
    public void TryFindEvidenceRequestReadsLatestWorkerResultField()
    {
        var verification = new TaskVerificationRecord(
            "review",
            "C:\\tmp",
            1,
            """
            WORKER_RESULT:
            blockers: stale blocker
            evidence-request: Core.Tests: OldTests
            verdict: needs-work
            END_WORKER_RESULT
            WORKER_RESULT:
            blockers: missing focused evidence
            evidence-request: Infrastructure.Tests: FullyQualifiedName~ConductorDriverTests
            verdict: needs-work
            END_WORKER_RESULT
            """,
            "",
            DateTimeOffset.UtcNow,
            WorkerResultPresent: true);

        Assert.True(WorkerResultBlockers.TryFindEvidenceRequest(verification, out var request));
        Assert.Equal("Infrastructure.Tests: FullyQualifiedName~ConductorDriverTests", request);
    }
}
