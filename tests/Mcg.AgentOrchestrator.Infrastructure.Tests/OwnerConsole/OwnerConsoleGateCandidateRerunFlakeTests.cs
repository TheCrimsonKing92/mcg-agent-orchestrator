using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each loader reads only its fixture's files and injected clock.
public sealed class OwnerConsoleGateCandidateRerunFlakeTests
{
    private const string Failure = "Sample.Tests.RealFailure.Fails";
    private const string Target = "infrastructure tests: Remainder";
    private const string Focused = "reviewer focused evidence: Infrastructure.Tests FullyQualifiedName~" + Failure;
    private const string Prefix = "31bcecc5-gate-20261029153416830-1a60276764bb4900b24d041376df0054";
    private static readonly string Tree = new('a', 40);
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 16, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("same", true)]
    [InlineData("different", false)]
    [InlineData("missing-rerun-tree", false)]
    [InlineData("missing-candidate-tree", false)]
    [InlineData("missing-kind", false)]
    [InlineData("incomplete-coverage", false)]
    public void OnlyPassingCandidateRerunOnSameTreeCoveringEveryFailureShowsFlake(string scenario, bool flake)
    {
        using var fixture = new Fixture();
        if (scenario == "incomplete-coverage")
            fixture.WriteResults(Target, "", (Failure, "Failed"), ("Sample.Tests.OtherFailure.Fails", "Failed"));
        var gate = fixture.Load(
            scenario == "missing-candidate-tree" ? null : Tree,
            scenario == "missing-rerun-tree" ? null : scenario == "different" ? new string('b', 40) : Tree,
            scenario == "missing-kind" ? null : "candidate-rerun");
        var failed = Assert.Single(gate.Lanes, row => !row.IsAttribution);
        var confirmation = Assert.IsType<OwnerConsoleGateConfirmation>(failed.Confirmation);
        Assert.True(confirmation.Results?.AllPassed, "The negative controls must actually read a passing TRX.");
        Assert.Equal(flake, confirmation.NotReproduced);
        foreach (var expanded in new[] { false, true })
        {
            var lines = OwnerConsoleGateLayout.Lines(gate, 240, expanded).ToArray();
            Assert.EndsWith(flake ? " | FLAKE, re-run passed" : " | FAILING", lines[0]);
            Assert.Equal(flake, lines.Any(line => line.Contains("passed on re-run (flake)", StringComparison.Ordinal)));
        }
    }

    [Theory]
    [InlineData("not-reproduced", "passed", null)]
    [InlineData("reproduced", "failed", null)]
    [InlineData("invalid-evidence", "passed", "assembly-outside-root")]
    [InlineData("not-run", "unknown", null)]
    public void MemberReproductionIsNamedAndNeverConfirmsCandidateEvenOnSameTree(
        string result, string outcome, string? reason)
    {
        using var fixture = new Fixture();
        var gate = fixture.Load(Tree, Tree, "member-reproduction", result, outcome, reason);
        var failed = Assert.Single(gate.Lanes, lane => !lane.IsAttribution);
        Assert.Null(failed.Confirmation);
        var reproduction = Assert.Single(failed.Reproductions!);
        Assert.Equal("31bcecc5", reproduction.MemberId);
        Assert.Equal(result, reproduction.Result);
        Assert.Equal(reason, reproduction.Reason);
        foreach (var expanded in new[] { false, true })
        {
            var lines = OwnerConsoleGateLayout.Lines(gate, 240, expanded).ToArray();
            Assert.EndsWith(" | FAILING", lines[0]);
            Assert.Contains(lines, line => line.Contains("reproduction 31bcecc5: " + result, StringComparison.Ordinal));
            Assert.DoesNotContain(lines, line => line.Contains("flake", StringComparison.OrdinalIgnoreCase));
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "candidate-flake-" + Guid.NewGuid().ToString("N"));

        internal Fixture()
        {
            Directory.CreateDirectory(_directory);
            WriteResults(Target, "", (Failure, "Failed"));
            WriteResults(Focused, "-run-1", (Failure, "Passed"));
        }

        internal void WriteResults(string target, string suffix, params (string Name, string Outcome)[] results)
        {
            var path = Path.Combine(_directory, AcceptanceArtifactFileNames.BoundFileName(
                AcceptanceArtifactFileNames.SanitizeFileName(Prefix + "." + AcceptanceArtifactFileNames.Slug(target) + suffix), ".trx"));
            new XDocument(new XElement("TestRun", new XElement("Results", results.Select(result =>
                new XElement("UnitTestResult", new XAttribute("testName", result.Name), new XAttribute("outcome", result.Outcome)))),
                new XElement("ResultSummary", new XElement("Counters", new XAttribute("total", results.Length),
                    new XAttribute("failed", results.Count(item => item.Outcome == "Failed")), new XAttribute("notExecuted", 0))))).Save(path);
        }

        internal OwnerConsoleGateProgress Load(string? candidateTree, string? rerunTree, string? kind,
            string? reproductionResult = null, string outcome = "passed", string? reason = null)
        {
            var manifest = Path.Combine(_directory, "manifest.json");
            File.WriteAllText(manifest, """
                { "engine": { "infrastructureTestLanes": [ { "name": "Remainder", "estimatedSerialSeconds": 800 } ] } }
                """);
            var events = new List<string>
            {
                Progress(-60, Target, "remainder", "failed", candidateTree, "candidate", null),
                Progress(-47, Focused, "remainder-run-1", outcome, rerunTree, kind,
                    reproductionResult is null ? null : "31bcecc5")
            };
            if (reproductionResult is not null)
                events.Add(JsonSerializer.Serialize(new { timestamp = Now.AddSeconds(-46), eventKind = "train-reproduction",
                    goalId = "31bcecc5", detail = "MERGE_TRAIN_REPRODUCTION evidence=" + JsonSerializer.Serialize(
                        new MergeTrainReproductionOutcome("31bcecc5", reproductionResult, reason, [Failure], null)) }));
            var log = Path.Combine(_directory, "conduct-events.log");
            File.WriteAllLines(log, events);
            var loader = new OwnerConsoleGateProgressLoader(log, manifest,
                Path.Combine(_directory, "missing-history.jsonl"), new OwnerConsoleTestClock { Now = Now });
            var gate = Assert.IsType<OwnerConsoleGateProgress>(loader.Load(new Dictionary<string, string>()));
            Assert.True(gate.Live);
            return gate;
        }

        private string Progress(int age, string target, string slug, string outcome, string? tree, string? kind, string? member) =>
            JsonSerializer.Serialize(new { timestamp = Now.AddSeconds(age), eventKind = "gate-progress", goalId = "31bcecc5",
                detail = $"PHASE_PROGRESS goal=31bcecc5 phase=check-complete target=\"{target}\" elapsed_ms=0 outcome={outcome} " +
                    $"heartbeat=\"{Path.Combine(_directory, Prefix + "." + slug + ".gate-heartbeat.json")}\"" +
                    (tree is null ? "" : " tree=" + tree) + (kind is null ? "" : " evidence_kind=" + kind) +
                    (member is null ? "" : " reproduction_member=" + member) });

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
