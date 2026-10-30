// Parallel-safe: each test owns all Git and filesystem state through its fixture.
public sealed class ImpactReplayChangedTestClassTests
{
    [Fact]
    public void ChangedTestWithoutSourceDependencyAddsItsClassAndLane()
    {
        using var fixture = new ImpactReplayFixture(changedFooTests: true);
        fixture.AddAttempt("changed-test");

        var (report, markdown) = fixture.Run();

        var attempt = Assert.Single(report.Attempts);
        Assert.Equal("Resolved", attempt.SelectionOutcome);
        Assert.Null(attempt.DegradationKind);
        Assert.Contains("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/FooTests.cs", attempt.ChangedFiles);
        Assert.Equal(new[] { "AlphaTests", "FooTests" }, attempt.SelectedTestClasses);
        Assert.Equal(new[] { "Alpha lane", "Chaos gate", "Foo lane" }, attempt.WouldRunLanes);
        Assert.Equal(new[] { "Beta lane" }, attempt.SkippedLanes);
        Assert.Equal(1, report.AttemptsGainingChangedTestClasses);
        Assert.Contains("Attempts whose selection gained changed test classes: 1", markdown);
        Assert.True(attempt.MissCheckComplete);
    }

    [Fact]
    public void GainedCountIncludesClassesWhoseLaneAlreadyWouldRun()
    {
        using var fixture = new ImpactReplayFixture(changedTestFiles: new Dictionary<string, string>
        {
            ["FloorTests.cs"] = """
                namespace SampleTests;
                public class FloorTests { [Xunit.Fact] public void Changed() { } }
                """
        });
        fixture.AddAttempt("already-selected-lane");

        var (report, _) = fixture.Run();

        var attempt = Assert.Single(report.Attempts);
        Assert.Equal("Resolved", attempt.SelectionOutcome);
        Assert.Equal(new[] { "AlphaTests", "FloorTests" }, attempt.SelectedTestClasses);
        Assert.Equal(new[] { "Alpha lane", "Chaos gate" }, attempt.WouldRunLanes);
        Assert.Equal(new[] { "Beta lane" }, attempt.SkippedLanes);
        Assert.Equal(1, report.AttemptsGainingChangedTestClasses);
    }

    [Fact]
    public void UnresolvedChangedTestDeclarationsAbandonSkipping()
    {
        using var fixture = new ImpactReplayFixture(changedTestFiles: new Dictionary<string, string>
        {
            ["Helpers.cs"] = "namespace SampleTests; internal class Helpers { }"
        });
        fixture.AddAttempt("no-test-declarations");

        var (report, _) = fixture.Run();

        var attempt = Assert.Single(report.Attempts);
        Assert.Equal("Abandoned", attempt.SelectionOutcome);
        Assert.Equal("ChangedTestClassUnresolved", attempt.DegradationKind);
        Assert.Empty(attempt.SkippedLanes);
        Assert.Equal(attempt.RanLanes, attempt.WouldRunLanes);
        Assert.Contains(attempt.EvidenceWarnings, warning => warning.Contains("Helpers.cs", StringComparison.Ordinal));
        Assert.False(attempt.MissCheckComplete);
    }
}
