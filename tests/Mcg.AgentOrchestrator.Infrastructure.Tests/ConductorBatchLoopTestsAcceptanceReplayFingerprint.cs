using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: fingerprints and snapshot restores use only case-owned state.
public sealed class ConductorBatchLoopTestsAcceptanceReplayFingerprint : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsAcceptanceReplayFingerprint(ITestOutputHelper output) : base(output) { }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void SnapshotRestorePreservesReplayIdentityAndFindingEvidence(bool hasFindingEvidence)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Preserve acceptance replay identity across restore");
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
            ["Replay the same candidate"], VerificationClass.TestVerifiable, [], []));
        var task = goal.Tasks.Single();
        var plainFingerprint = ConductorParallelAcceptanceAttemptCoordinator.BuildReplayContextFingerprint(goal);
        FindingEvidenceRequestDisposition[]? dispositions = null;
        if (hasFindingEvidence)
        {
            dispositions = [new("finding", "Infrastructure.Tests:ReplayTests", "pending-execution",
                "Awaiting focused evidence", FindingEvidenceRequestDecision.PendingExecution,
                new string('a', 40), "round", ExecutionBasisIdentity: "basis")];
            kernel.RecordFindingEvidenceRequestDispositions(goal.Id, task.Id, dispositions);
            Assert.NotEqual(plainFingerprint,
                ConductorParallelAcceptanceAttemptCoordinator.BuildReplayContextFingerprint(goal));
        }

        var fingerprint = ConductorParallelAcceptanceAttemptCoordinator.BuildReplayContextFingerprint(goal);
        var restored = AgentOrchestratorKernel.FromSnapshot(JsonSerializer.Deserialize<OrchestratorSnapshot>(
            JsonSerializer.Serialize(kernel.ExportSnapshot()))!).GetGoal(goal.Id);

        Assert.NotNull(fingerprint);
        Assert.Equal(fingerprint, ConductorParallelAcceptanceAttemptCoordinator.BuildReplayContextFingerprint(restored));
        var restoredDispositions = restored.Tasks.Single().LastVerification!.FindingEvidenceRequestDispositions;
        if (dispositions is null)
            Assert.Null(restoredDispositions);
        else
            Assert.Equal(dispositions, restoredDispositions);
    }
}
