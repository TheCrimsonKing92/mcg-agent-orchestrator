using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Infrastructure;

// The fixture changes the process-wide attempt prefix, so use the acceptance collection.
[Collection(TestCollections.GoalAcceptanceVerifier)]
public sealed class GoalAcceptanceVerifierCrosswalkSelectionBatchingTests
{
    [Fact]
    public async Task WholeSuite_600Ids_RunsOnceWithoutSelectorsAndCoversEveryDiscoveredId()
    {
        var ids = LongIds();
        using var fixture = new CrosswalkAcceptanceFixture();
        fixture.Discovery = CrosswalkAcceptanceFixture.List(ids);
        fixture.Output = Report(ids);

        var check = await fixture.RunAsync();

        Assert.True(check.Passed, check.OutputTail);
        Assert.Equal(2, fixture.Calls.Count);
        Assert.Contains(" list ", fixture.Calls[0].Command);
        Assert.Equal(CrosswalkClient.Command(fixture.Binary, "run", fixture.Root), fixture.Calls[1].Command);
        Assert.DoesNotContain("--id", fixture.Calls[1].Command);
        Assert.Equal("crosswalk: selected=600 results=600", check.ResultSummary);
        Assert.Equal(600, check.ExecutedTestCount);
        Assert.Single(check.NormalizedTestResultPaths!);
    }

    [Fact]
    public async Task WholeSuite_599Results_NamesTheMissingDiscoveredId()
    {
        var ids = LongIds();
        using var fixture = new CrosswalkAcceptanceFixture();
        fixture.Discovery = CrosswalkAcceptanceFixture.List(ids);
        fixture.Output = Report(ids[..^1]);

        var check = await fixture.RunAsync();

        Assert.False(check.Passed);
        Assert.Equal(2, fixture.Calls.Count);
        Assert.DoesNotContain("--id", fixture.Calls[1].Command);
        var diagnostic = Assert.Single(check.CrosswalkDiagnostics!, d => d.Category == CrosswalkDiagnosticCategory.MissingCoverage);
        Assert.Contains(ids[^1], diagnostic.Detail);
        Assert.DoesNotContain(ids[0], diagnostic.Detail);
        Assert.Equal("crosswalk: selected=600 results=599", check.ResultSummary);
    }

    [Fact]
    public async Task Explicit600Ids_BatchesSerializedCommandsInOrderAndMergesWithOneBudget()
    {
        var ids = LongIds();
        var clock = new SelectionClock();
        using var fixture = new CrosswalkAcceptanceFixture(ids, clock: clock);
        fixture.Discovery = CrosswalkAcceptanceFixture.List(ids);
        fixture.Handler = (command, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            return Task.FromResult(fixture.Result(IsList(command) ? fixture.Discovery : Report(SelectedIds(command))));
        };
        Assert.True(CrosswalkClient.Command(fixture.Binary, "run", fixture.Root,
            string.Concat(ids.Select(CrosswalkClient.IdArgument))).Length > 30_000);
        Assert.Equal(7_500, CrosswalkAcceptanceCheck.RunCommandLineLimit);

        var check = await fixture.RunAsync();

        Assert.True(check.Passed, check.OutputTail);
        var runs = fixture.Calls.Skip(1).ToArray();
        Assert.True(runs.Length > 1);
        Assert.All(runs, run =>
        {
            Assert.True(run.Command.Length < CrosswalkAcceptanceCheck.RunCommandLineLimit);
            Assert.Contains(" run ", run.Command);
            Assert.NotEmpty(SelectedIds(run.Command));
            Assert.Equal(CrosswalkClient.Command(fixture.Binary, "run", fixture.Root,
                string.Concat(SelectedIds(run.Command).Select(CrosswalkClient.IdArgument))), run.Command);
        });
        Assert.Equal(ids, runs.SelectMany(run => SelectedIds(run.Command)).ToArray());
        for (var index = 1; index < fixture.Calls.Count; index++)
            Assert.Equal(fixture.Calls[index - 1].Budget - TimeSpan.FromSeconds(1), fixture.Calls[index].Budget);
        Assert.Equal("crosswalk: selected=600 results=600", check.ResultSummary);
        var path = Assert.Single(check.NormalizedTestResultPaths!);
        Assert.Equal(ids, new NormalizedTestResultSource().Read(path).Records.Select(record => record.Id).ToArray());
    }

    [Fact]
    public async Task ExplicitSubset_RequiresOnlySelectedDiscoveredIds()
    {
        var ids = LongIds();
        using var fixture = new CrosswalkAcceptanceFixture([ids[41], ids[2]]);
        fixture.Discovery = CrosswalkAcceptanceFixture.List(ids);
        fixture.Output = Report([ids[41], ids[2]]);

        var check = await fixture.RunAsync();

        Assert.True(check.Passed, check.OutputTail);
        Assert.Equal(new[] { ids[41], ids[2] }, SelectedIds(fixture.Calls[1].Command));
        Assert.Equal(600, check.DiscoveredTestCount);
        Assert.Equal("crosswalk: selected=2 results=2", check.ResultSummary);
    }

    [Fact]
    public async Task LaterBatchMissingResult_IsCheckedAfterMerging()
    {
        var ids = LongIds();
        using var fixture = SelectedFixture(ids);
        fixture.Handler = (command, _) => Task.FromResult(fixture.Result(IsList(command)
            ? fixture.Discovery : Report(SelectedIds(command).Where(id => id != ids[^1]))));

        var check = await fixture.RunAsync();

        Assert.False(check.Passed);
        Assert.True(fixture.Calls.Count > 2);
        var missing = Assert.Single(check.CrosswalkDiagnostics!, d => d.Category == CrosswalkDiagnosticCategory.MissingCoverage);
        Assert.Contains(ids[^1], missing.Detail);
        Assert.DoesNotContain(ids[0], missing.Detail);
        Assert.Equal("crosswalk: selected=600 results=599", check.ResultSummary);
    }

    [Fact]
    public async Task SecondBatchStartFailure_StopsAndDoesNotPublishPartialCoverage()
    {
        var ids = LongIds();
        using var fixture = SelectedFixture(ids);
        fixture.Handler = (command, _) =>
        {
            if (fixture.Calls.Count == 3)
                throw new CommandLineTooLongException(9_000, 8_191, "crosswalk");
            return Task.FromResult(fixture.Result(IsList(command) ? fixture.Discovery : Report(SelectedIds(command))));
        };

        var check = await fixture.RunAsync();

        Assert.False(check.Passed);
        Assert.Equal(3, fixture.Calls.Count);
        var diagnostic = Assert.Single(check.CrosswalkDiagnostics!);
        Assert.Equal(CrosswalkDiagnosticCategory.StartFailed, diagnostic.Category);
        Assert.Contains("batch 2/", diagnostic.Detail);
        Assert.Contains(nameof(CommandLineTooLongException), diagnostic.Detail);
        Assert.Null(check.ExitCode);
        Assert.Empty(check.NormalizedTestResultPaths!);
    }

    [Fact]
    public async Task OversizedSingleton_IsRejectedWithoutAnyRun()
    {
        var id = new string('x', CrosswalkAcceptanceCheck.RunCommandLineLimit);
        using var fixture = SelectedFixture([id]);

        var check = await fixture.RunAsync();

        Assert.False(check.Passed);
        Assert.Single(fixture.Calls);
        var diagnostic = Assert.Single(check.CrosswalkDiagnostics!);
        Assert.Equal(CrosswalkDiagnosticCategory.StartFailed, diagnostic.Category);
        Assert.Contains("position 1", diagnostic.Detail);
        Assert.Null(check.ExitCode);
        Assert.Empty(check.NormalizedTestResultPaths!);
    }

    [Fact]
    public async Task FailingTests_DoNotPreventLaterBatchesFromExecuting()
    {
        var ids = LongIds();
        using var fixture = SelectedFixture(ids);
        fixture.Handler = (command, _) => Task.FromResult(fixture.Result(IsList(command) ? fixture.Discovery
            : CrosswalkAcceptanceFixture.Report(SelectedIds(command).Select(id => (id, id == ids[0] ? "failed" : "passed")).ToArray()),
            !IsList(command) && SelectedIds(command).Contains(ids[0]) ? 1 : 0));

        var check = await fixture.RunAsync();

        Assert.False(check.Passed);
        Assert.Equal(ids, fixture.Calls.Skip(1).SelectMany(call => SelectedIds(call.Command)).ToArray());
        Assert.Equal(1, check.ExitCode);
        Assert.Equal(600, check.ExecutedTestCount);
        Assert.Equal(ids[0], Assert.Single(check.FailingTestIdentities!));
        Assert.DoesNotContain(check.CrosswalkDiagnostics!, d => d.Category == CrosswalkDiagnosticCategory.MissingCoverage);
        Assert.Single(check.NormalizedTestResultPaths!);
    }

    [Fact]
    public async Task SharedDeadlineExhaustedBetweenBatches_DoesNotLaunchAnotherBatch()
    {
        var ids = LongIds();
        var clock = new SelectionClock();
        using var fixture = new CrosswalkAcceptanceFixture(ids, clock: clock);
        fixture.Discovery = CrosswalkAcceptanceFixture.List(ids);
        fixture.Handler = (command, _) =>
        {
            if (!IsList(command)) clock.Advance(TimeSpan.FromMinutes(2));
            return Task.FromResult(fixture.Result(IsList(command) ? fixture.Discovery : Report(SelectedIds(command))));
        };

        var check = await fixture.RunAsync();

        Assert.False(check.Passed);
        Assert.Equal(2, fixture.Calls.Count);
        Assert.Contains(check.CrosswalkDiagnostics!, d => d.Category == CrosswalkDiagnosticCategory.Timeout && d.Detail.Contains("batch 2/"));
        Assert.Empty(check.NormalizedTestResultPaths!);
    }

    [Fact]
    public async Task DuplicateResultAcrossBatches_IsMalformedAndNotPublished()
    {
        var ids = LongIds();
        using var fixture = SelectedFixture(ids);
        fixture.Handler = (command, _) => Task.FromResult(fixture.Result(IsList(command) ? fixture.Discovery
            : Report(fixture.Calls.Count == 3 ? SelectedIds(command).Append(ids[0]) : SelectedIds(command))));

        var check = await fixture.RunAsync();

        Assert.False(check.Passed);
        Assert.Contains(check.CrosswalkDiagnostics!, d => d.Category == CrosswalkDiagnosticCategory.MalformedResults);
        Assert.Empty(check.NormalizedTestResultPaths!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UndiscoveredResultId_IsMalformedInBothPaths(bool explicitSelection)
    {
        using var fixture = new CrosswalkAcceptanceFixture(explicitSelection ? ["known"] : []);
        fixture.Discovery = CrosswalkAcceptanceFixture.List("known");
        fixture.Output = Report(["known", "KNOWN"]);

        var check = await fixture.RunAsync();

        Assert.False(check.Passed);
        Assert.Contains(check.CrosswalkDiagnostics!, d => d.Category == CrosswalkDiagnosticCategory.MalformedResults);
    }

    private static string[] LongIds() => Enumerable.Range(0, 600).Select(index =>
        $"src/features/selection-{index:D4}/crosswalk-regression.test.ts::suite's \"quoted case\" > handles scenario {index:D4}").ToArray();

    private static CrosswalkAcceptanceFixture SelectedFixture(string[] ids)
    {
        var fixture = new CrosswalkAcceptanceFixture(ids) { Discovery = CrosswalkAcceptanceFixture.List(ids) };
        fixture.Handler = (command, _) => Task.FromResult(fixture.Result(IsList(command) ? fixture.Discovery : Report(SelectedIds(command))));
        return fixture;
    }

    private static bool IsList(string command) => command.Contains(" list ", StringComparison.Ordinal);
    private static string[] SelectedIds(string command) => Regex.Matches(command, @" --id '((?:[^']|'')*)'")
        .Select(match => match.Groups[1].Value.Replace("''", "'", StringComparison.Ordinal)).ToArray();
    private static string Report(IEnumerable<string> ids) => CrosswalkAcceptanceFixture.Report(ids.Select(id => (id, "passed")).ToArray());

    private sealed class SelectionClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        internal void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }
}
