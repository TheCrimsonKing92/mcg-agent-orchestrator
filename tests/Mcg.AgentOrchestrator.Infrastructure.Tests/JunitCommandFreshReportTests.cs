using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: all state and process callbacks belong to the fixture.
public sealed class JunitCommandFreshReportTests
{
    [Fact]
    public async Task FreshAndStaleReports_PublishesOnlyFreshRows_AndPreservesStale()
    {
        using var fixture = new JunitCommandFixture("component", ["reports/*.xml", "reports/**/*.xml"]);
        var source = Path.Combine(fixture.Root, "component", "src", "math.case");
        var stale = fixture.WriteReport("reports/stale.xml", JunitCommandFixture.Now.AddHours(-1), (source, "stale", false));
        var staleBytes = File.ReadAllBytes(stale);
        var staleWrite = File.GetLastWriteTimeUtc(stale);
        fixture.WriteReports = () => fixture.WriteReport("reports/fresh.xml", JunitCommandFixture.Now,
            (source, "adds", false), (source, "subtracts", true));
        fixture.ExitCode = 1;

        var check = await fixture.RunAsync();

        Assert.False(check.Passed);
        Assert.Equal(1, check.ExitCode);
        Assert.Equal(["component/src/math.case::adds", "component/src/math.case::subtracts"],
            JunitCommandFixture.ReadRows(check).Select(row => row.Id));
        Assert.Equal(["component/src/math.case::subtracts"], check.FailingTestIdentities);
        Assert.Equal(staleBytes, File.ReadAllBytes(stale));
        Assert.Equal(staleWrite, File.GetLastWriteTimeUtc(stale));
        Assert.EndsWith("fresh.xml", Assert.Single(fixture.ParsedReports));
        Assert.Equal(2, fixture.Calls.Count);
        Assert.All(fixture.Calls, call => Assert.Equal(Path.GetFullPath(fixture.ProjectRoot), call.Root));
        Assert.All(fixture.Calls, call => Assert.True(call.Token.CanBeCanceled));
        Assert.NotNull(check.CrosswalkDiagnostics);
        Assert.NotNull(check.CohortEvidence);
        Assert.Equal("junit-attempt", check.TestResultAttemptId);
        Assert.Equal(0, check.TestResultRunOrdinal);
        Assert.Equal(check.NormalizedTestResultPaths, AcceptanceVerificationResult.CollectTestResultPaths([check]));
        Assert.All(JunitCommandFixture.ReadRows(check), row =>
        {
            Assert.Equal("component/src/math.case", row.File);
            Assert.Equal("original message", row.Message);
            Assert.Equal("original trace", row.Trace);
            Assert.Equal(["suite"], row.SuitePath);
            Assert.Equal(12, row.DurationMs);
            Assert.Equal(2, Assert.Single(row.Attempts).DurationMs);
            Assert.Equal("seed", row.Seed);
            Assert.Equal("output", row.OutputRef);
            Assert.Equal("fixture", row.ReportedBy);
        });
    }

    [Fact]
    public async Task MultipleFreshReports_MergesInOrdinalOrder_AndRebasesRelativeSources()
    {
        using var fixture = new JunitCommandFixture("component", ["reports/**/*.xml", "reports/*.xml"]);
        fixture.WriteReports = () =>
        {
            fixture.WriteReport("reports/z.xml", JunitCommandFixture.Now, ("src/z.case", "last", false));
            fixture.WriteReport("reports/a/first.xml", JunitCommandFixture.Now, ("src/first.case", "first", false));
        };
        var check = await fixture.RunAsync();
        Assert.True(check.Passed, check.OutputTail);
        Assert.Equal(["component/src/first.case::first", "component/src/z.case::last"], JunitCommandFixture.ReadRows(check).Select(row => row.Id));
        Assert.Equal(2, fixture.ParsedReports.Count);
        Assert.EndsWith(Path.Combine("a", "first.xml"), fixture.ParsedReports[0]);
    }
}
