using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: unique directories and execution owners; Crosswalk processes are injected.
public sealed class CrosswalkUnmatchedSelectorRefusalTests
{
    [Fact]
    public async Task AllRefused_ListsTenDistinctOrdinalFilesAndStartsNoRun()
    {
        var ids = Enumerable.Range(0, 12).Reverse().Select(index => $"spec/{index:D2}_spec.lua::Suite case").ToArray();
        using var fixture = new Fixture(ids.Append(ids[0].Replace("case", "second", StringComparison.Ordinal)).ToArray());
        var check = fixture.Build("NoSuchSuite", "AlsoAbsent");
        var result = await fixture.RunAsync(check);
        Assert.Equal(CrosswalkClient.Command(fixture.Binary, "list", fixture.Root), Assert.Single(fixture.Calls));
        Assert.False(result.Passed);
        Assert.Equal(0, result.ExecutedTestCount);
        Assert.Empty(result.NormalizedTestResultPaths!);
        var expectedFiles = string.Join(", ", Enumerable.Range(0, 10).Select(index => $"spec/{index:D2}_spec.lua"));
        var diagnostics = result.CrosswalkDiagnostics!;
        Assert.Equal(2, diagnostics.Count);
        Assert.Equal("selector matched no discovered test: NoSuchSuite; discovered test files: " + expectedFiles, diagnostics[0].Detail);
        Assert.Equal("selector matched no discovered test: AlsoAbsent; discovered test files: " + expectedFiles, diagnostics[1].Detail);
        Assert.Contains(diagnostics[0].Detail, result.ResultSummary);
        Assert.True(CrosswalkSelectorResolution.TryBuildRejection("request", [check], [result], out var rejection));
        Assert.Equal(FocusedEvidenceRejectionCode.UnresolvableSelection, rejection.Code);
        Assert.Contains(diagnostics[0].Detail, rejection.Detail);
        Assert.Contains(diagnostics[1].Detail, rejection.Detail);
    }

    [Fact]
    public async Task AllRefused_FocusedRequestReturnsRefusalInsteadOfCandidateInconclusive()
    {
        using var fixture = new Fixture(["spec/a_spec.lua::Suite case"]);
        var settings = AcceptanceGateEngineSettings.Parse(CrosswalkFocusedEvidenceTests.Manifest);
        await using var owner = new AcceptanceFocusedVerificationOwner(
            new("focused-refusal", "goal", fixture.Root, "candidate", Path.Combine(fixture.Root, "focused"), null), settings);
        var result = await FocusedEvidenceExecution.RunOwnedFocusedEvidenceAsync(owner,
            async (arm, sha, _, _, checks, _, _, _, _, _, _, _, _) =>
            {
                var checkResult = await fixture.RunAsync(Assert.Single(checks));
                return new(arm, sha, FindingEvidenceArmDisposition.Inconclusive, true, false, "candidate inconclusive", [checkResult]);
            },
            (_, _, _, _, _, _, _, _, _, _, _) => throw new InvalidOperationException("Refusal must not run a baseline."),
            (_, _, _, _, _, _, _, _, _, _, _, _, _) => throw new InvalidOperationException("Refusal must not run a negative control."),
            _ => null, fixture.Root, null, ".:NoSuchSuite");
        Assert.False(result.Accepted);
        Assert.False(result.Passed);
        Assert.Equal(FocusedEvidenceRejectionCode.UnresolvableSelection, result.Rejection!.Code);
        Assert.Contains("selector matched no discovered test: NoSuchSuite", result.Summary);
        Assert.Null(result.OutcomeReason);
        Assert.Null(result.Arms);
        Assert.Equal(CrosswalkClient.Command(fixture.Binary, "list", fixture.Root), Assert.Single(fixture.Calls));
    }

    [Fact]
    public async Task MixedSelection_RunsOnlyMatchesAndPreservesRefusalAndSuiteGrade()
    {
        string[] selected = ["spec/object.lua::PZISObjectUtils clone copies", "spec/other.lua::PZISObjectUtils equals compares"];
        using var fixture = new Fixture([selected[0], "spec/other.lua::PZISObjectUtilsExtra case", selected[1]]);
        var check = fixture.Build("PZISObjectUtils", "NoSuchSuite");
        var result = await fixture.RunAsync(check);
        Assert.Equal(2, fixture.Calls.Count);
        Assert.Equal(selected, Fixture.SelectedIds(fixture.Calls[1]));
        Assert.Equal(2, result.ExecutedTestCount);
        Assert.False(result.Passed);
        Assert.Contains("selector matched no discovered test: NoSuchSuite", Assert.Single(result.CrosswalkDiagnostics!).Detail);
        Assert.Contains("selector matched no discovered test: NoSuchSuite", result.ResultSummary);
        Assert.False(CrosswalkSelectorResolution.TryBuildRejection("request", [check], [result], out _));
        var read = new NormalizedTestResultSource().Read(Assert.Single(result.NormalizedTestResultPaths!));
        Assert.Equal(TestResultReadStatus.Readable, read.Status);
        Assert.Equal(selected, read.Records.Select(record => record.Id));
        Assert.Equal(CrosswalkFocusedEvidence.SelectionGrade.Satisfied, CrosswalkFocusedEvidence.Grade(read.Records, "PZISObjectUtils"));
        Assert.Equal(CrosswalkFocusedEvidence.SelectionGrade.NotDemonstrated, CrosswalkFocusedEvidence.Grade(read.Records, "NoSuchSuite"));
    }

    [Theory]
    [InlineData("NoSuchSuite")]
    [InlineData("spec/absent.lua")]
    [InlineData("spec/a_spec.lua::absent")]
    public async Task UnmatchedForms_RefuseAfterSuccessfulEmptyDiscovery(string selector)
    {
        using var fixture = new Fixture([]);
        var check = fixture.Build(selector);
        var result = await fixture.RunAsync(check);
        Assert.Equal(CrosswalkClient.Command(fixture.Binary, "list", fixture.Root), Assert.Single(fixture.Calls));
        Assert.Equal("selector matched no discovered test: " + selector + "; discovered test files: ", Assert.Single(result.CrosswalkDiagnostics!).Detail);
        Assert.True(CrosswalkSelectorResolution.TryBuildRejection("request", [check], [result], out _));
    }

    [Fact]
    public async Task DiscoveryFailure_IsNotMisreportedAsSelectorRefusal()
    {
        using var fixture = new Fixture([]) { DiscoveryJson = "not json" };
        var check = fixture.Build("NoSuchSuite");
        var result = await fixture.RunAsync(check);
        Assert.Single(fixture.Calls);
        Assert.Contains(result.CrosswalkDiagnostics!, d => d.Category == CrosswalkDiagnosticCategory.InvalidJson);
        Assert.False(CrosswalkSelectorResolution.TryBuildRejection("request", [check], [result], out _));
    }

    private sealed class Fixture(string[] discovered) : IDisposable
    {
        internal string Root { get; } = InfrastructureTestSupport.CreateTempDirectory();
        internal string Binary => Path.Combine(Root, "crosswalk.exe");
        internal List<string> Calls { get; } = [];
        internal string DiscoveryJson { get; init; } = CrosswalkAcceptanceFixture.List(discovered);

        internal Check Build(params string[] selectors)
        {
            var settings = AcceptanceGateEngineSettings.Parse(CrosswalkFocusedEvidenceTests.Manifest);
            Assert.True(CrosswalkFocusedEvidence.TryBuild(string.Join(";", selectors.Select(selector => ".:" + selector)), settings, Root,
                out var checks, out _, out var rejection), rejection.Detail);
            return Assert.Single(checks);
        }

        internal async Task<AcceptanceCheckResult> RunAsync(Check check)
        {
            File.WriteAllText(Binary, "injected process boundary");
            var resolver = new CrosswalkExecutableResolver(Binary, Root,
                (_, _) => Task.FromResult(new WorkerProcessRunResult(0, "test-version", "")));
            await using var owner = new AcceptanceAttemptExecutionOwner(
                new("refusal-attempt", "goal", Root, "tree", "main", "commit", Path.Combine(Root, "receipts", "attempt"), null, Environment.ProcessId, null),
                new AcceptanceGateEngineSettings());
            return await CrosswalkAcceptanceCheck.RunOwnedAsync(check, Root, owner, owner.CreateInvocation(check.Name),
                TimeProvider.System, resolver, false,
                _ => new(null, "test", check.Name, null, Path.Combine(Root, "heartbeat"), null, "stub", null),
                (arguments, _, _, _, _, _) =>
                {
                    var command = arguments[^1];
                    Calls.Add(command);
                    var json = command.Contains(" list ", StringComparison.Ordinal) ? DiscoveryJson
                        : CrosswalkAcceptanceFixture.Report(SelectedIds(command).Select(id => (id, "passed")).ToArray());
                    return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, json) { StandardOutput = json });
                }, CancellationToken.None);
        }

        internal static string[] SelectedIds(string command) => Regex.Matches(command, @" --id '((?:[^']|'')*)'")
            .Select(match => match.Groups[1].Value.Replace("''", "'", StringComparison.Ordinal)).ToArray();

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
