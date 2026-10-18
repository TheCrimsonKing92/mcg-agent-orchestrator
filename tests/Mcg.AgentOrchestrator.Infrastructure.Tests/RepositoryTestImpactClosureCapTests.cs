using Mcg.AgentOrchestrator.Core;

// Parallel-safe: read-only repository census; no process or shared state mutation.
public sealed class RepositoryTestImpactClosureCapTests
{
    [Fact]
    public void RealRepository_InfrastructureClosureIsBelowIndexedSourceCap()
    {
        var measured = RepositoryTestImpactClosure.MeasureIndexedSources(VerifiedRepositoryRoot.Find());
        Assert.True(measured is not null, "Infrastructure test source closure could not be measured");
        var count = measured.IndexedSourceFileCount!.Value;
        var cap = RepositoryTestImpactClosure.DefaultIndexedSourceFileCap;
        var violation = CapViolation(count, cap);
        Assert.True(violation is null, violation);
    }

    [Fact]
    public void CapViolation_AtCap_NamesBoundAndRemediation()
    {
        Assert.Equal("Infrastructure test source closure has 4000 files, at or above " +
            "ReverseDependencyTestImpactReader.MaximumIndexedSourceFiles = 4000; focused selection " +
            "abandons for the full suite once a candidate adds a file. Raise the cap or shrink the closure.",
            CapViolation(4000, 4000));
        Assert.Null(CapViolation(3999, 4000));
    }

    private static string? CapViolation(int count, int cap) => count < cap ? null :
        $"Infrastructure test source closure has {count} files, at or above " +
        $"ReverseDependencyTestImpactReader.MaximumIndexedSourceFiles = {cap}; focused selection " +
        "abandons for the full suite once a candidate adds a file. Raise the cap or shrink the closure.";
}
