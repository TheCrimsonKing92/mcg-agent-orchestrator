using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: each test owns its repository and replay artifacts.
public sealed class ImpactReplayUnmappedTestClassReasonTests
{
    [Fact]
    public void ReplayReportsEachReasonWithAttemptAndMatchedNames()
    {
        using var fixture = new ImpactReplayFixture(changedTestFiles: new Dictionary<string, string>
        {
            ["OrphanTests.cs"] = "namespace SampleTests; public class OrphanTests { [CustomFact] public void Works() { } }",
            ["GammaTests.cs"] = "namespace SampleTests; public class GammaTests { [Xunit.Fact] public void Works() { } }",
            ["Nested/AlphaTests.cs"] = "namespace SampleTests.Nested; public class AlphaTests { [Xunit.Fact] public void Works() { } }"
        });
        fixture.AddAttempt("all-reasons");

        var (report, _) = fixture.Run();

        var diagnostics = Assert.IsType<ImpactReplayUnmappedTestClasses>(report.UnmappedTestClasses);
        Assert.Equal(3, diagnostics.Occurrences.Count);
        Assert.All(diagnostics.Occurrences, item => Assert.Equal("all-reasons", item.AttemptId));
        var missing = Assert.Single(diagnostics.Occurrences, item => item.Class == "OrphanTests");
        Assert.Equal("not-scanned", missing.Reason);
        Assert.Empty(missing.FullNames);
        var noLane = Assert.Single(diagnostics.Occurrences, item => item.Class == "GammaTests");
        Assert.Equal("no-lane", noLane.Reason);
        Assert.Equal(new[] { "SampleTests.GammaTests" }, noLane.FullNames);
        var ambiguous = Assert.Single(diagnostics.Occurrences, item => item.Class == "AlphaTests");
        Assert.Equal("ambiguous", ambiguous.Reason);
        Assert.Equal(new[] { "SampleTests.AlphaTests", "SampleTests.Nested.AlphaTests" }, ambiguous.FullNames);
    }

    [Fact]
    public void AmbiguousClassWithLanesKeepsResolvedSelection()
    {
        using var fixture = new ImpactReplayFixture(changedTestFiles: new Dictionary<string, string>
        {
            ["Nested/AlphaTests.cs"] = "namespace SampleTests.Nested; public class AlphaTests { [Xunit.Fact] public void Works() { } }"
        });
        fixture.AddAttempt("ambiguous-resolved");

        var (report, _) = fixture.Run();

        var attempt = Assert.Single(report.Attempts);
        Assert.Equal("Resolved", attempt.SelectionOutcome);
        Assert.Null(attempt.DegradationKind);
        Assert.Equal(new[] { "Alpha lane", "Chaos gate" }, attempt.WouldRunLanes);
        Assert.Equal(new[] { "Beta lane" }, attempt.SkippedLanes);
        var diagnostic = Assert.Single(report.UnmappedTestClasses!.Occurrences);
        Assert.Equal("ambiguous-resolved", diagnostic.AttemptId);
        Assert.Equal("AlphaTests", diagnostic.Class);
        Assert.Equal("ambiguous", diagnostic.Reason);
        Assert.Equal(new[] { "SampleTests.AlphaTests", "SampleTests.Nested.AlphaTests" }, diagnostic.FullNames);
    }

    [Fact]
    public void SourceBoundAbandonmentStillReportsAmbiguousClass()
    {
        using var fixture = new ImpactReplayFixture(changedSourceCount: 6, changedTestFiles: new Dictionary<string, string>
        {
            ["Nested/AlphaTests.cs"] = "namespace SampleTests.Nested; public class AlphaTests { [Xunit.Fact] public void Works() { } }"
        });
        fixture.AddAttempt("source-bound");

        var (report, _) = fixture.Run();

        var attempt = Assert.Single(report.Attempts);
        Assert.Equal("Abandoned", attempt.SelectionOutcome);
        Assert.Equal("ChangedSourceFileCount", attempt.DegradationKind);
        Assert.Empty(attempt.SkippedLanes);
        var diagnostic = Assert.Single(report.UnmappedTestClasses!.Occurrences);
        Assert.Equal("source-bound", diagnostic.AttemptId);
        Assert.Equal("AlphaTests", diagnostic.Class);
        Assert.Equal("ambiguous", diagnostic.Reason);
        Assert.Equal(new[] { "SampleTests.AlphaTests", "SampleTests.Nested.AlphaTests" }, diagnostic.FullNames);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AmbiguityTakesPrecedenceRegardlessOfLaneMembership(bool hasLane)
    {
        var diagnostic = ImpactReplayUnmappedTestClasses.Classify("attempt", "SameTests",
            ["Z.SameTests", "A.SameTests"], hasLane);
        Assert.NotNull(diagnostic);
        Assert.Equal("ambiguous", diagnostic.Reason);
        Assert.Equal(new[] { "A.SameTests", "Z.SameTests" }, diagnostic.FullNames);
    }
}
