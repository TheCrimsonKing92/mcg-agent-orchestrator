using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class ApparatusRedClassifierCandidateRerunTests
{
    [Theory]
    [InlineData(true, false, ApparatusInfrastructureSignatures.EvidenceKind)]
    [InlineData(false, true, ApparatusRedClassifier.CrossGoalEvidenceKind)]
    public void ExistingApparatusEvidenceKeepsItsDispositionWhenRerunFails(
        bool signature, bool crossGoal, string expectedKind)
    {
        var failingTest = new ApparatusRedFailingTest(
            "check", "Sample.Tests.UnchangedTests.Fails", signature ? "known-signature" : null,
            ["tests/Sample.Tests/UnchangedTests.cs"], false, crossGoal,
            CandidateRerunFailed: true);

        var result = ApparatusRedClassifier.Classify(new ApparatusRedEvidence(
            ["src/Changed.cs"], [failingTest], true, 0, 2));

        Assert.Equal(expectedKind, Assert.IsType<ApparatusRedDisposition.Regate>(result).EvidenceKind);
        var passedResult = ApparatusRedClassifier.Classify(new ApparatusRedEvidence(
            ["src/Changed.cs"],
            [failingTest with { CandidateRerunFailed = false, CandidateRerunPassed = true }],
            true, 0, 2));
        Assert.Equal(expectedKind, Assert.IsType<ApparatusRedDisposition.Regate>(passedResult).EvidenceKind);
    }
}
