using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Evidence = ProspectiveEvidenceTrxRowsSelectorFormTests.Evidence;

// Parallel-safe: each fixture owns its kernel snapshot, source module and receipts.
public sealed class GateTestObligationEvaluatorSelectorFormTests
{
    [Fact]
    public void Evaluate_SoloNestedAndBareEvidence_SatisfiesBothObligations()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var paths = Evidence.WriteSelectorReceipts(fixture.Root);
        var attempt = fixture.WriteAttempt([], additionalPaths: paths);
        var snapshot = fixture.Kernel.ExportSnapshot();
        var kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = [snapshot.Goals.Single() with
            {
                GateTestObligations =
                [
                    new("nested-class", fixture.Goal.AuthoritativeBrief.Version, [Evidence.NestedSelector],
                        null, "Nested evidence", DateTimeOffset.UnixEpoch),
                    new("go-bare", fixture.Goal.AuthoritativeBrief.Version, [Evidence.GoSelector],
                        null, "Go evidence", DateTimeOffset.UnixEpoch)
                ]
            }]
        });

        var results = GateTestObligationEvaluator.Evaluate(kernel.GetGoal(fixture.Goal.Id),
            fixture.OrchestratorDirectory, fixture.CandidateSha);

        Assert.Equal(new[] { "nested-class", "go-bare" }, results.Select(result => result.Obligation.Id));
        Assert.All(results, result =>
        {
            Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, result.Outcome);
            Assert.Equal(attempt.Id, result.SourceId);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Evaluate_AmbiguousPassedGoName_ReportsCandidatesInSelectorOrder(bool includeMissing)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var path = Evidence.WriteGoReceipt(fixture.Root, "example.com/m",
            ("b", "b", "TestShared"), ("a", "a", "TestShared"));
        var attempt = fixture.WriteAttempt([], additionalPaths: [path]);
        string[] selectors = includeMissing ? ["TestMissingFirst", "TestShared", "TestMissingLast"] : ["TestShared"];
        var kernel = GateTestObligationIntakeTests.Seed(fixture.Kernel, fixture.Goal, selectors);

        var result = Assert.Single(GateTestObligationEvaluator.Evaluate(kernel.GetGoal(fixture.Goal.Id),
            fixture.OrchestratorDirectory, fixture.CandidateSha));

        Assert.Equal(GateTestObligationEvaluator.Outcome.SelectorNotFound, result.Outcome);
        const string ambiguous = "TestShared (ambiguous: example.com/m/a, example.com/m/b)";
        string[] expected = includeMissing ? ["TestMissingFirst", ambiguous, "TestMissingLast"] : [ambiguous];
        Assert.Equal(expected, result.Selectors);
        Assert.Equal(attempt.Id, result.SourceId);
    }
}
