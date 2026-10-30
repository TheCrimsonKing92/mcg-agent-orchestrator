using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: result files and attempt metadata live under a unique fixture root.
public sealed class GateTestObligationEvaluatorCtrfTests
{
    [Fact]
    public void Evaluate_CtrfOnlySource_SatisfiesTwoFileObligation()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var selectors = new[] { ProspectiveEvidenceCtrfRowsTests.EngineFile, ProspectiveEvidenceCtrfRowsTests.UiFile };
        var kernel = GateTestObligationIntakeTests.Seed(fixture.Kernel, fixture.Goal, selectors);
        var goal = kernel.GetGoal(fixture.Goal.Id);
        // Grouped stores require bound gate evidence; use the solo CTRF-only attempt source.
        var directory = Path.Combine(fixture.OrchestratorDirectory, "acceptance-gate-attempts", goal.Id.Value);
        var path = ProspectiveEvidenceCtrfRowsTests.CopyFixture(directory);
        const string sourceId = "ctrf-only";
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

        var result = Assert.Single(GateTestObligationEvaluator.Evaluate(
            goal, fixture.OrchestratorDirectory, fixture.CandidateSha));

        Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, result.Outcome);
        Assert.Equal(sourceId, result.SourceId);
        Assert.Equal(selectors, result.Selectors);
    }
}
