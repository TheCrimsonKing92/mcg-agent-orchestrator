using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns a unique ledger directory and injects any write failure.
public sealed class AcceptanceLaneFlakeLedgerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("lane-flakes-").FullName;

    [Xunit.Fact]
    public void Read_BothOutcomes_FiltersAndSkipsUnreadableRows()
    {
        var ledger = new AcceptanceLaneFlakeLedger(_root);
        var now = DateTimeOffset.Parse("2026-10-09T20:00:00Z");
        var first = Row("Alpha", "flake", now);
        var second = Row("Alpha", "confirmed-failure", now.AddMinutes(1));
        ledger.Append(first);
        var path = Path.Combine(_root, ".orchestrator", "lane-flakes.jsonl");
        File.AppendAllLines(path, ["broken json", "{}", "null", "{\"recordedAt\":\"invalid\"}"]);
        ledger.Append(second);
        ledger.Append(Row("Beta", "flake", now.AddMinutes(2)));

        var all = ledger.Read();
        Assert.Equal(3, all.Count);
        var alpha = ledger.Read("Alpha", now);
        Assert.Equal(2, alpha.Count);
        AssertRow(first, alpha[0]);
        AssertRow(second, alpha[1]);
        AssertRow(second, Assert.Single(ledger.Read("Alpha", now.AddSeconds(1))));
        Assert.Single(ledger.Read("Beta"));
        Assert.Empty(ledger.Read("Absent"));
        Assert.Empty(ledger.Read(since: now.AddMinutes(3)));
        Assert.Empty(new AcceptanceLaneFlakeLedger(Path.Combine(_root, "absent")).Read());
    }

    [Xunit.Theory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public async Task RunAsync_LedgerWriteThrows_PreservesVerdictAndLogsOnce(bool passes)
    {
        var check = new GoalAcceptanceVerifier.AcceptanceManifestCheck
        {
            Name = "infrastructure tests: Alpha", Type = "dotnet-test",
            Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
            Arguments = ["--filter", "FullyQualifiedName~AlphaTests"]
        };
        var first = new AcceptanceCheckResult(check.Name, false, 1, null,
            TestResultPaths: ["first.trx"], FailingTestIdentities: ["First.Failed"], TestResultRunOrdinal: 1,
            CompletionDecision: new(false, "failing-trx", false, 1, 1, 1, "Failed"));
        var second = first with
        {
            Passed = passes, ExitCode = passes ? 0 : 1, TestResultPaths = ["second.trx"],
            FailingTestIdentities = passes ? [] : ["Second.Failed"], TestResultRunOrdinal = 2,
            CompletionDecision = new(passes, passes ? null : "failing-trx", false, passes ? 0 : 1, 1, 1, passes ? "Passed" : "Failed")
        };
        var lines = new List<string>();
        var writes = 0;
        var ledger = new AcceptanceLaneFlakeLedger(_root, _ => { writes++; throw new IOException("injected write failure"); });
        var actual = await new AcceptanceLaneTestFailureRerun(ledger, emit: lines.Add).RunAsync(
            check, first, () => Task.FromResult(second), r => $"invocation-{r.TestResultRunOrdinal}");
        var expected = await new AcceptanceLaneTestFailureRerun(emit: _ => { }).RunAsync(
            check, first, () => Task.FromResult(second), r => $"invocation-{r.TestResultRunOrdinal}");

        Assert.Equal(1, writes);
        Assert.Equal(expected.ResultSummary, actual.ResultSummary);
        Assert.Equal(expected.CompletionDecision, actual.CompletionDecision);
        Assert.Equal(expected.TestResultPaths, actual.TestResultPaths);
        Assert.Equal(expected.FailingTestIdentities, actual.FailingTestIdentities);
        Assert.Equal(expected.LaneRerun!.Outcome, actual.LaneRerun!.Outcome);
        Assert.Equal(expected.LaneRerun.FirstInvocationId, actual.LaneRerun.FirstInvocationId);
        Assert.Equal(expected.LaneRerun.FirstTestResultPaths, actual.LaneRerun.FirstTestResultPaths);
        Assert.Equal(expected.LaneRerun.FirstFailingTestIdentities, actual.LaneRerun.FirstFailingTestIdentities);
        Assert.Equal(expected.LaneRerun.RerunInvocationId, actual.LaneRerun.RerunInvocationId);
        Assert.Equal(expected.LaneRerun.RerunTestResultPaths, actual.LaneRerun.RerunTestResultPaths);
        Assert.Equal(expected.LaneRerun.RerunFailingTestIdentities, actual.LaneRerun.RerunFailingTestIdentities);
        Assert.Equal(passes, actual.Passed);
        Assert.Single(lines, line => line.StartsWith("LANE_FLAKE_LEDGER_WRITE_FAILED ", StringComparison.Ordinal));
        Assert.Single(lines, line => line.StartsWith("LANE_RERUN ", StringComparison.Ordinal));
    }

    private static AcceptanceLaneFlakeRow Row(string lane, string outcome, DateTimeOffset at) =>
        AcceptanceLaneFlakeRow.FromEvidence("goal", "attempt", lane, "candidate-tree",
            new("first-id", ["first.trx"], ["First.Failed"], "failing-trx", "rerun-id", ["rerun.trx"],
                outcome == "flake" ? [] : ["Second.Failed"], outcome == "flake" ? null : "failing-trx", outcome), at);

    private static void AssertRow(AcceptanceLaneFlakeRow expected, AcceptanceLaneFlakeRow actual)
    {
        Assert.Equal(expected.GoalId, actual.GoalId);
        Assert.Equal(expected.AttemptId, actual.AttemptId);
        Assert.Equal(expected.LaneName, actual.LaneName);
        Assert.Equal(expected.CandidateTreeSha, actual.CandidateTreeSha);
        Assert.Equal(expected.FirstInvocationId, actual.FirstInvocationId);
        Assert.Equal(expected.FirstTestResultPaths, actual.FirstTestResultPaths);
        Assert.Equal(expected.FirstFailingTestIdentities, actual.FirstFailingTestIdentities);
        Assert.Equal(expected.FirstPredicate, actual.FirstPredicate);
        Assert.Equal(expected.RerunInvocationId, actual.RerunInvocationId);
        Assert.Equal(expected.RerunTestResultPaths, actual.RerunTestResultPaths);
        Assert.Equal(expected.RerunFailingTestIdentities, actual.RerunFailingTestIdentities);
        Assert.Equal(expected.RerunPredicate, actual.RerunPredicate);
        Assert.Equal(expected.Outcome, actual.Outcome);
        Assert.Equal(expected.RecordedAt, actual.RecordedAt);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
