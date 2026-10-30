using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: each fixture owns its directory and execution owner; all processes are stubbed.
public sealed class CrosswalkFocusedEvidenceSelectionTests
{
    [Fact]
    public async Task FileSelection_FocusedCheck_RunsOnlyDiscoveredTestsInThatFile()
    {
        const string file = CrosswalkFocusedEvidenceTests.File;
        string[] selected = [file + "::first", file + "::second"];
        using var fixture = new Fixture([selected[0], "src/unrelated.test.ts::other", selected[1]]);
        var settings = AcceptanceGateEngineSettings.Parse(CrosswalkFocusedEvidenceTests.Manifest);
        var request = new FindingEvidenceRequest([new(".", file)]);
        Assert.True(FindingEvidenceRequestNormalization.TryNormalize(request, settings,
            (_, _) => throw new InvalidOperationException("Crosswalk must not expand .NET sibling classes."),
            out var normalized, out var executorRequest, out _, out var detail), detail);
        Assert.Equal(new FindingEvidenceSelection("crosswalk selection", file),
            Assert.Single(normalized.Selections));
        Assert.Equal("crosswalk selection:" + file, executorRequest);
        var check = fixture.Build(executorRequest, out var coverage);

        Assert.Equal("crosswalk-test", check.Type);
        Assert.Equal(".", check.Project);
        Assert.Equal([file], check.Arguments);
        Assert.True(check.IsFocusedEvidenceSelection);
        Assert.True(CrosswalkFocusedEvidence.IsEvidenceCheck(check));
        Assert.Equal("crosswalk-focused", coverage.ExecutionReason);
        Assert.Equal(executorRequest, Assert.Single(coverage.TargetToChecks).Target);

        var result = await fixture.RunAsync(check);

        Assert.True(result.Passed, result.OutputTail);
        Assert.Equal(2, fixture.Calls.Count);
        Assert.Equal(CrosswalkClient.Command(fixture.Binary, "list", fixture.Root), fixture.Calls[0]);
        Assert.Equal(CrosswalkClient.Command(fixture.Binary, "run", fixture.Root,
            string.Concat(selected.Select(CrosswalkClient.IdArgument))), fixture.Calls[1]);
        Assert.Equal(selected, Fixture.SelectedIds(fixture.Calls[1]));
        Assert.Equal(2, result.ExecutedTestCount);
        var read = new NormalizedTestResultSource().Read(Assert.Single(result.NormalizedTestResultPaths!));
        Assert.Equal(TestResultReadStatus.Readable, read.Status);
        Assert.Equal(selected, read.Records.Select(record => record.Id));
        Assert.Empty(FocusedEvidenceNotRunPlan.Compare([check], [result], coverage, true).Selections);
    }

    [Fact]
    public async Task OversizeFocusedSelection_ExistingRunner_BatchesAndMergesExactIds()
    {
        var ids = Enumerable.Range(0, 200).Select(index =>
            $"src/selection-{index:D4}/regression.test.ts::suite's quoted case > scenario {index:D4}").ToArray();
        using var fixture = new Fixture(ids.Append("src/unselected.test.ts::other").ToArray());
        var request = string.Join(";", ids.Select(id => ".:" + id));
        var check = fixture.Build(request, out var coverage);
        Assert.Equal(ids, check.Arguments);
        Assert.Equal(ids.Length, coverage.TargetToChecks.Count);
        Assert.True(CrosswalkClient.Command(fixture.Binary, "run", fixture.Root,
            string.Concat(ids.Select(CrosswalkClient.IdArgument))).Length > CrosswalkAcceptanceCheck.RunCommandLineLimit);

        var result = await fixture.RunAsync(check);

        Assert.True(result.Passed, result.OutputTail);
        Assert.Equal(CrosswalkClient.Command(fixture.Binary, "list", fixture.Root), fixture.Calls[0]);
        var runs = fixture.Calls.Skip(1).ToArray();
        Assert.True(runs.Length > 1, "Focused ids must use the existing bounded batch runner.");
        Assert.All(runs, command =>
        {
            Assert.True(command.Length <= CrosswalkAcceptanceCheck.RunCommandLineLimit);
            var selected = Fixture.SelectedIds(command);
            Assert.NotEmpty(selected);
            Assert.Equal(CrosswalkClient.Command(fixture.Binary, "run", fixture.Root,
                string.Concat(selected.Select(CrosswalkClient.IdArgument))), command);
        });
        Assert.Equal(ids, runs.SelectMany(Fixture.SelectedIds));
        Assert.Equal(ids.Length, result.ExecutedTestCount);
        var read = new NormalizedTestResultSource().Read(Assert.Single(result.NormalizedTestResultPaths!));
        Assert.Equal(TestResultReadStatus.Readable, read.Status);
        Assert.Equal(ids, read.Records.Select(record => record.Id));
    }

    private sealed class Fixture(string[] discovered) : IDisposable
    {
        internal string Root { get; } = InfrastructureTestSupport.CreateTempDirectory();
        internal string Binary => Path.Combine(Root, "crosswalk.exe");
        internal List<string> Calls { get; } = [];

        internal Check Build(string request, out FocusedEvidenceCoverage coverage)
        {
            var settings = AcceptanceGateEngineSettings.Parse(CrosswalkFocusedEvidenceTests.Manifest);
            Assert.True(GoalAcceptanceVerifier.TryBuildFocusedEvidenceChecks(request, settings, Root,
                out var checks, out coverage, out var rejection), rejection.Detail);
            return Assert.Single(checks);
        }

        internal async Task<AcceptanceCheckResult> RunAsync(Check check)
        {
            File.WriteAllText(Binary, "injected process boundary");
            var resolver = new CrosswalkExecutableResolver(Binary, Root,
                (_, _) => Task.FromResult(new WorkerProcessRunResult(0, "test-version", "")));
            await using var owner = new AcceptanceAttemptExecutionOwner(
                new("focused-attempt", "goal", Root, "tree", "main", "commit",
                    Path.Combine(Root, "receipts", "attempt"), null, Environment.ProcessId, null),
                new AcceptanceGateEngineSettings());
            return await CrosswalkAcceptanceCheck.RunOwnedAsync(check, Root, owner,
                owner.CreateInvocation(check.Name), TimeProvider.System, resolver, false,
                _ => new(null, "test", check.Name, null, Path.Combine(Root, "heartbeat"), null, "stub", null),
                (arguments, root, _, _, _, _) =>
                {
                    Assert.Equal(Root, root);
                    var command = arguments[^1];
                    Calls.Add(command);
                    var json = command.Contains(" list ", StringComparison.Ordinal)
                        ? CrosswalkAcceptanceFixture.List(discovered)
                        : CrosswalkAcceptanceFixture.Report(SelectedIds(command).Select(id => (id, "passed")).ToArray());
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, json) { StandardOutput = json });
                }, CancellationToken.None);
        }

        internal static string[] SelectedIds(string command) => Regex.Matches(command, @" --id '((?:[^']|'')*)'")
            .Select(match => match.Groups[1].Value.Replace("''", "'", StringComparison.Ordinal)).ToArray();

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
