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
        Xunit.Assert.Equal(consensus.Trim(), result.SelectedContract.Plan);
        Xunit.Assert.Equal(PlannerCandidateSelector.SelectionSignal, result.Receipt.SelectionSignal);
        Xunit.Assert.True(result.Receipt.CandidateScores[1] > result.Receipt.CandidateScores[0]);
        Xunit.Assert.Contains(result.Receipt.Sections, section =>
            section.Section.Equals("verification commands and classes", StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadPlannerFixture(string repositoryRoot) => File.ReadAllText(Path.Combine(
        repositoryRoot,
        "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests", "Fixtures", "PlannerOutputContract",
        "658501ce-f6708f44-20260805012800.out.txt"));
}
