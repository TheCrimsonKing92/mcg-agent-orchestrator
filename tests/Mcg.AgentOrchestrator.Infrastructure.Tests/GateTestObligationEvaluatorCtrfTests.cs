using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: result files and grouped SQLite receipt stores live under a unique fixture root.
public sealed class GateTestObligationEvaluatorCtrfTests
{
    [Theory]
    [InlineData("solo")]
    [InlineData("merge-train")]
    [InlineData("cohort")]
    public void Evaluate_CtrfOnlySource_SatisfiesTwoFileObligation(string sourceKind)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var selectors = new[] { ProspectiveEvidenceCtrfRowsTests.EngineFile, ProspectiveEvidenceCtrfRowsTests.UiFile };
        var kernel = GateTestObligationIntakeTests.Seed(fixture.Kernel, fixture.Goal, selectors);
        var goal = kernel.GetGoal(fixture.Goal.Id);
        string sourceId;
        string path;
        if (sourceKind == "solo")
        {
            var directory = Path.Combine(fixture.OrchestratorDirectory, "acceptance-gate-attempts", goal.Id.Value);
            path = ProspectiveEvidenceCtrfRowsTests.CopyFixture(directory);
            sourceId = "ctrf-only";
            File.WriteAllText(Path.Combine(directory, sourceId + ".attempt.json"), JsonSerializer.Serialize(new
            {
                attemptId = sourceId, goalId = goal.Id.Value,
                kind = ConductorParallelAcceptanceAttemptCoordinator.GateDispatchKind,
                branchHeadSha = fixture.CandidateSha, outcome = ConductorParallelAcceptanceAttemptOutcome.Passed,
                completedAt = DateTimeOffset.UnixEpoch, testResultPaths = new[] { Path.GetFileName(path) }
            }));
            var attempt = Assert.Single(ProspectiveEvidenceGateResolver.ReadAttempts(
                fixture.OrchestratorDirectory, goal.Id, fixture.CandidateSha));
            Assert.Equal(new[] { path }, attempt.Paths);
        }
        else
        {
            path = ProspectiveEvidenceCtrfRowsTests.CopyFixture(fixture.Root);
            var receipt = ProspectiveEvidenceGateResolverGroupedReceiptTests.Save(
                fixture, [path], cohort: sourceKind == "cohort");
            sourceId = receipt.ReceiptId;
            Assert.Equal(new[] { path }, Assert.Single(GroupedGateReceiptTrxEvidence.ReadPassedReceipts(
                fixture.OrchestratorDirectory, goal.Id, fixture.CandidateSha)).TrxPaths);
        }

        var result = Assert.Single(GateTestObligationEvaluator.Evaluate(
            goal, fixture.OrchestratorDirectory, fixture.CandidateSha));

        Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, result.Outcome);
        Assert.Equal(sourceId, result.SourceId);
        Assert.Equal(selectors, result.Selectors);
    }
}
