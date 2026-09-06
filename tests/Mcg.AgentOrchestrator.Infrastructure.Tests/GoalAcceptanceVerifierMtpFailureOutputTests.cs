using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GoalAcceptanceVerifierMtpFailureOutputTests
{
    [Xunit.Fact]
    public void GreenReceiptReportsPostRunExitEvidence()
    {
        var result = new GoalAcceptanceVerifier.CommandResult(
            1,
            """
            Test run summary: Passed! - Mcg.AgentOrchestrator.Infrastructure.Tests.dll (net10.0|x64)
              total: 373
              failed: 0
              succeeded: 373
            [FATAL ERROR] Foreground threads were left running, forcing process exit
            """);

        var output = GoalAcceptanceVerifier.BuildMtpFailureOutputForTests(
            "infrastructure tests: Worker dispatch fixtures",
            result,
            [MtpFixturePath("mtp-xunit-v3-green-run.trx.xml")]);

        Assert.Contains("373 of 373 tests executed", output, StringComparison.Ordinal);
        Assert.Contains("the shard process exited 1 after the run completed", output, StringComparison.Ordinal);
        Assert.Contains("predicate=nonzero-exit", output, StringComparison.Ordinal);
        Assert.Contains("runner reported: \"[FATAL ERROR] Foreground threads were left running, forcing process exit\"", output, StringComparison.Ordinal);
        Assert.DoesNotContain("exited before tests ran", output, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void GreenReceiptDoesNotInventRunnerCause()
    {
        var result = new GoalAcceptanceVerifier.CommandResult(
            1,
            "Test run summary: Passed! - Mcg.AgentOrchestrator.Infrastructure.Tests.dll");

        var output = GoalAcceptanceVerifier.BuildMtpFailureOutputForTests(
            "infrastructure tests: Worker dispatch fixtures",
            result,
            [MtpFixturePath("mtp-xunit-v3-green-run.trx.xml")]);

        Assert.Contains("run summary reported Passed!", output, StringComparison.Ordinal);
        Assert.DoesNotContain("runner reported:", output, StringComparison.Ordinal);
        Assert.DoesNotContain("foreground thread", output, StringComparison.OrdinalIgnoreCase);
    }

    [Xunit.Fact]
    public void TimedOutGreenReceiptDoesNotReportPostRunExit()
    {
        var result = new GoalAcceptanceVerifier.CommandResult(
            1,
            "Test run summary: Passed! - Mcg.AgentOrchestrator.Infrastructure.Tests.dll",
            TimedOut: true,
            Timeout: TimeSpan.FromMinutes(5));

        var output = GoalAcceptanceVerifier.BuildMtpFailureOutputForTests(
            "infrastructure tests: Worker dispatch fixtures",
            result,
            [MtpFixturePath("mtp-xunit-v3-green-run.trx.xml")]);

        Assert.Contains("predicate=timed-out", output, StringComparison.Ordinal);
        Assert.DoesNotContain("after the run completed", output, StringComparison.Ordinal);
        Assert.DoesNotContain("exited before tests ran", output, StringComparison.Ordinal);
    }

    [Xunit.Fact]
    public void AbortedReceiptAvoidsPreRunClaim()
    {
        var result = new GoalAcceptanceVerifier.CommandResult(1, "Runner exited after reporting one test.");

        var output = GoalAcceptanceVerifier.BuildMtpFailureOutputForTests(
            "infrastructure tests: Worker dispatch fixtures",
            result,
            [MtpFixturePath("mtp-xunit-v3-aborted-run.trx.xml")]);

        Assert.Contains("TRX reports 1 of 1 tests executed (outcome=Aborted)", output, StringComparison.Ordinal);
        Assert.Contains("predicate=failing-trx", output, StringComparison.Ordinal);
        Assert.DoesNotContain("exited before tests ran", output, StringComparison.Ordinal);
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void ZeroOrUnknownExecutedCountRetainsPreRunPossibility(bool includeZeroCounters)
    {
        var trxPath = Path.Combine(Path.GetTempPath(), $"mtp-empty-{Guid.NewGuid():N}.trx");
        try
        {
            File.WriteAllText(
                trxPath,
                includeZeroCounters
                    ? """
                      <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                        <ResultSummary outcome="Completed">
                          <Counters total="0" executed="0" passed="0" failed="0" notExecuted="0" />
                        </ResultSummary>
                      </TestRun>
                      """
                    : """
                      <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                        <ResultSummary outcome="Completed" />
                      </TestRun>
                      """);

            var output = GoalAcceptanceVerifier.BuildMtpFailureOutputForTests(
                "infrastructure tests: empty shard",
                new GoalAcceptanceVerifier.CommandResult(1, "Runner exited without reporting a test."),
                [trxPath]);

            Assert.Contains(
                "TRX found but contained no failure records (process may have exited before tests ran)",
                output,
                StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(trxPath);
        }
    }

    [Xunit.Theory]
    [Xunit.InlineData(0, false)]
    [Xunit.InlineData(1, true)]
    [Xunit.InlineData(2, false)]
    public void CensusOnlyRunsForForcedExit(int exitCode, bool expected)
    {
        Assert.Equal(expected, AssemblyExitThreadCensus.ShouldReportForExitCode(exitCode));
    }

    [Xunit.Fact]
    public void CensusReportsTotalAndNewestThreadsWhenDetailsAreTruncated()
    {
        var threads = Enumerable.Range(0, 20)
            .Select(id => (Id: id, StartedAt: new DateTime(2026, 1, 1).AddSeconds(id)))
            .ToArray();

        var census = AssemblyExitThreadCensus.ProjectNewestThreadsForReport(
            threads,
            thread => thread.StartedAt,
            thread => $"id:{thread.Id}");
        var diagnostic = AssemblyExitThreadCensus.BuildCensusDiagnostic(1, census);

        Assert.Equal(20, census.TotalCount);
        Assert.Equal(16, census.Threads.Count);
        Assert.Equal("id:19", census.Threads[0]);
        Assert.Equal("id:4", census.Threads[^1]);
        Assert.Contains("new_os_threads=20 reported_os_threads=16 truncated=true", diagnostic, StringComparison.Ordinal);
    }

    private static string MtpFixturePath(string fileName) =>
        Path.Combine(
            InfrastructureTestSupport.FindRepositoryRoot(),
            "tests",
            "Mcg.AgentOrchestrator.Infrastructure.Tests",
            "TestData",
            "Fixtures",
            fileName);
}
