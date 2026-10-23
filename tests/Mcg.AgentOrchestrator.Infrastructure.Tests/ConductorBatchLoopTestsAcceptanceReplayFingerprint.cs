using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: fingerprints and snapshot restores use only case-owned state.
public sealed class ConductorBatchLoopTestsAcceptanceReplayFingerprint : ConductorBatchLoopTests
{
    public ConductorBatchLoopTestsAcceptanceReplayFingerprint(ITestOutputHelper output) : base(output) { }

    [Xunit.Fact]
    public void FingerprintIgnoresRebindAuditFieldsButNotObligationContract()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Keep replay identity across candidate-only rebinds");
        var version = kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
            ["Replay the same candidate"], VerificationClass.TestVerifiable, [], [])).Version;
        var prior = new string('a', 40);
        var candidate = new string('b', 40);
        var original = kernel.MapCriterionEvidenceOwner(goal.Id, 0, version, CriterionEvidenceOwner.Acceptance,
            "operator binding", expectedCandidateSha: prior);
        var fingerprint = ConductorParallelAcceptanceAttemptCoordinator.BuildReplayContextFingerprint(goal);
        Assert.NotNull(fingerprint);
        Assert.Null(original.FindingStableId);

        var rebound = kernel.MapCriterionEvidenceOwner(goal.Id, 0, version, CriterionEvidenceOwner.Acceptance,
            "conductor ratchet conflict resolution", findingStableId: "finding-rebind-control", expectedCandidateSha: candidate);

        Assert.Equal(original.Id, rebound.Id);
        Assert.Equal(prior, original.ExpectedCandidateSha);
        Assert.Equal(candidate, rebound.ExpectedCandidateSha);
        Assert.NotEqual(original.Provenance, rebound.Provenance);
        Assert.Equal("finding-rebind-control", rebound.FindingStableId);
        Assert.Equal(fingerprint, ConductorParallelAcceptanceAttemptCoordinator.BuildReplayContextFingerprint(goal));

        kernel.MapCriterionEvidenceOwner(goal.Id, 0, version, CriterionEvidenceOwner.Operator,
            "conductor ratchet conflict resolution", requiredScope: rebound.RequiredScope,
            findingStableId: "finding-rebind-control", expectedCandidateSha: candidate);
        var operatorFingerprint = ConductorParallelAcceptanceAttemptCoordinator.BuildReplayContextFingerprint(goal);
        Assert.NotEqual(fingerprint, operatorFingerprint);

        kernel.MapCriterionEvidenceOwner(goal.Id, 0, version, CriterionEvidenceOwner.Operator,
            "conductor ratchet conflict resolution", requiredScope: "operator observation",
            findingStableId: "finding-rebind-control", expectedCandidateSha: candidate);
        var scopeFingerprint = ConductorParallelAcceptanceAttemptCoordinator.BuildReplayContextFingerprint(goal);
        Assert.NotEqual(operatorFingerprint, scopeFingerprint);

        kernel.RecordGoalRefinement(goal.Id, goal.RefinedSpec! with { AcceptanceCriteria = ["Changed criterion text"] });
        Assert.NotEqual(scopeFingerprint, ConductorParallelAcceptanceAttemptCoordinator.BuildReplayContextFingerprint(goal));
    }

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
