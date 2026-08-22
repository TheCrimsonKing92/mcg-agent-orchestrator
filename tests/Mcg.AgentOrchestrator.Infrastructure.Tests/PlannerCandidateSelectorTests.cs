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

        var result = PlannerCandidateSelector.Select(
            [new(0, shortestOutlier), new(1, consensus), new(2, consensus)],
            repositoryRoot);

        Xunit.Assert.True(shortestOutlier.Length < consensus.Length);
        Xunit.Assert.Equal(1, result.Receipt.SelectedCandidateIndex);
        Xunit.Assert.Equal(
            consensus.Trim().ReplaceLineEndings("\n"),
            result.SelectedContract.Plan!.ReplaceLineEndings("\n"));
        Xunit.Assert.Equal(PlannerCandidateSelector.SelectionSignal, result.Receipt.SelectionSignal);
        Xunit.Assert.True(result.Receipt.CandidateScores[1] > result.Receipt.CandidateScores[0]);
        Xunit.Assert.Contains(result.Receipt.Sections, section =>
            section.Section.Equals("verification commands and classes", StringComparison.OrdinalIgnoreCase));
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
