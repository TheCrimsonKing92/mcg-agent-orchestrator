using Mcg.AgentOrchestrator.Infrastructure;

public sealed class PlannerCandidateSelectorTests
{
    [Xunit.Fact]
    public void PeerAgreementSelectsConsensusInsteadOfFirstShortestValidPlan()
    {
        var repositoryRoot = InfrastructureTestSupport.FindRepositoryRoot();
        var shortestOutlier = ReadPlannerFixture(repositoryRoot);
        var consensus = shortestOutlier.Replace(
            "## Risks and stop conditions",
            "The dispatch selection seam records candidate agreement for downstream role handoff and deterministic review evidence.\n\n## Risks and stop conditions",
            StringComparison.Ordinal);
        var consensusPeer = consensus.Replace(
            "deterministic review evidence.",
            "deterministic review evidence with independently phrased bounded details.",
            StringComparison.Ordinal);
        var selectedSourcePath = Path.Combine(repositoryRoot, "planner.sample-1.out.log");

        var result = PlannerCandidateSelector.Select(
            [
                new(0, shortestOutlier),
                new(1, consensus, SourcePath: selectedSourcePath),
                new(2, consensusPeer, SourcePath: Path.Combine(repositoryRoot, "planner.sample-2.out.log"))
            ],
            repositoryRoot);
        var expectedConsensus = PlannerOutputContract.Resolve(consensus, string.Empty, repositoryRoot).Plan!;

        Xunit.Assert.True(shortestOutlier.Length < consensus.Length);
        Xunit.Assert.NotEqual(consensus, consensusPeer);
        Xunit.Assert.Equal(1, result.Receipt.SelectedCandidateIndex);
        Xunit.Assert.Equal(
            expectedConsensus.ReplaceLineEndings("\n"),
            result.SelectedContract.Plan!.ReplaceLineEndings("\n"));
        Xunit.Assert.Equal(selectedSourcePath, result.SelectedContract.IngestedPath);
        Xunit.Assert.Equal(PlannerCandidateSelector.SelectionSignal, result.Receipt.SelectionSignal);
        Xunit.Assert.True(result.Receipt.CandidateScores[1] > result.Receipt.CandidateScores[0]);
        Xunit.Assert.Contains(result.Receipt.Sections, section =>
            section.Section.Equals("verification commands and classes", StringComparison.OrdinalIgnoreCase));
    }

    [Xunit.Fact]
    public void SparseCandidateIndexesFailWithTypedArgumentError()
    {
        var repositoryRoot = InfrastructureTestSupport.FindRepositoryRoot();
        var plan = ReadPlannerFixture(repositoryRoot);

        var exception = Xunit.Assert.Throws<ArgumentException>(() =>
            PlannerCandidateSelector.Select([new(0, plan), new(2, plan)], repositoryRoot));

        Xunit.Assert.Equal("candidates", exception.ParamName);
        Xunit.Assert.Contains("contiguous", exception.Message, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void DivergenceHashesIgnoreLineEndingDifferences()
    {
        var repositoryRoot = InfrastructureTestSupport.FindRepositoryRoot();
        var plan = ReadPlannerFixture(repositoryRoot).ReplaceLineEndings("\n");
        var divergent = plan.Replace(
            "## Risks and stop conditions",
            "A distinct dispatch seam changes the recorded evidence.\n\n## Risks and stop conditions",
            StringComparison.Ordinal);

        var result = PlannerCandidateSelector.Select(
            [new(0, divergent), new(1, plan), new(2, plan.ReplaceLineEndings("\r\n"))],
            repositoryRoot);

        var section = Xunit.Assert.Single(result.Receipt.Sections, candidate =>
            candidate.Section.Equals("verification commands and classes", StringComparison.OrdinalIgnoreCase));
        var firstConsensus = Xunit.Assert.Single(section.Candidates, candidate => candidate.CandidateIndex == 1);
        var secondConsensus = Xunit.Assert.Single(section.Candidates, candidate => candidate.CandidateIndex == 2);
        Xunit.Assert.Equal(firstConsensus.ContentHash, secondConsensus.ContentHash);
    }

    private static string ReadPlannerFixture(string repositoryRoot) => File.ReadAllText(Path.Combine(
        repositoryRoot,
        "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests", "Fixtures", "PlannerOutputContract",
        "658501ce-f6708f44-20260805012800.out.txt"));
}
