using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: pure matching and per-test temporary files with explicit timestamps.
public sealed class JunitReportGlobTests
{
    [Theory]
    [InlineData("reports/*.xml", "reports/a.xml", true)]
    [InlineData("reports/*.xml", "reports/nested/a.xml", false)]
    [InlineData("reports/?.xml", "reports/a.xml", true)]
    [InlineData("reports/?.xml", "reports/ab.xml", false)]
    [InlineData("reports/**/*.xml", "reports/a.xml", true)]
    [InlineData("reports/**/*.xml", "reports/a/b.xml", true)]
    [InlineData("reports/**/*.xml", "Reports/a.xml", false)]
    [InlineData("reports/**.xml", "reports/a/b.xml", true)]
    [InlineData("reports\\*.xml", "reports/a.xml", true)]
    [InlineData("./reports/*.xml", "reports/a.xml", true)]
    public void Wildcards_UseOrdinalDirectorySemantics(string glob, string path, bool matches) =>
        Assert.Equal(matches, JunitReportGlob.Matches(glob, path));

    [Fact]
    public void FreshnessBoundary_IsInclusive_AndOverlappingMatchesAreUnique()
    {
        using var fixture = new JunitCommandFixture();
        var fresh = fixture.WriteReport("reports/a.xml", JunitCommandFixture.Now);
        var stale = fixture.WriteReport("reports/stale.xml", JunitCommandFixture.Now.AddTicks(-1));
        var selected = JunitReportGlob.FreshFiles(Path.GetFullPath(fixture.ProjectRoot),
            ["reports/*.xml", "reports/**/*.xml", "./reports/*.xml"], JunitCommandFixture.Now, CancellationToken.None);
        Assert.Equal(fresh, Assert.Single(selected));
        Assert.True(File.Exists(stale));
    }
}
