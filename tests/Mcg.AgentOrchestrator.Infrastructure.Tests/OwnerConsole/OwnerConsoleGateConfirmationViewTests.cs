// Parallel-safe: each fixture owns a unique folder and uses an injected clock.
using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class OwnerConsoleGateConfirmationViewTests
{
    private const string Failure = "Sample.Tests.RealFailure.Fails";
    private const string Target = "infrastructure tests: Remainder";
    private const string Focused = "reviewer focused evidence: Infrastructure.Tests FullyQualifiedName~" + Failure;
    private const string Prefix = "31bcecc5-gate-20261029153416830-1a60276764bb4900b24d041376df0054";
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FailedLane_SixNotExecutedResults_ListsOnlyTheRealFailure()
    {
        using var fixture = new Fixture();
        var gate = fixture.Load();
        var lane = Assert.Single(gate.Lanes);
        Assert.Equal(new[] { "RealFailure.Fails" }, lane.FailingTestNames);
        Assert.Equal(6, lane.SkippedTestCount);

        var lines = Render(gate);
        Assert.Equal(new[] { "     RealFailure.Fails", "     (6 skipped, not failures)" },
            lines.Where(line => line.StartsWith("     ", StringComparison.Ordinal)));
        foreach (var index in Enumerable.Range(1, 6))
            Assert.DoesNotContain(lines, line => line.Contains("PlatformSkip.Case" + index, StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("more", StringComparison.Ordinal));
        Assert.EndsWith(" | FAILING", lines[0]);
    }

    [Fact]
    public void FailedLane_MixedOutcomes_PreservesEveryFailureInDocumentOrder()
    {
        using var fixture = new Fixture();
        fixture.WriteResults(Target, "",
            ("Sample.Zeta.First", "Failed"), ("Sample.Omitted.Unknown", "Inconclusive"),
            ("Sample.Alpha.Second", "Error"), ("Sample.Omitted.NotRun", "NotExecuted"),
            ("Sample.Beta.Third", "Timeout"), ("Sample.Omitted.Skip", "Skipped"),
            ("Sample.Zeta.First", " failed "), ("Sample.Omitted.Pass", "Passed"));
        var gate = fixture.Load();
        var lines = Render(gate);
        Assert.Equal(new[] { "Zeta.First", "Alpha.Second", "Beta.Third", "Zeta.First" },
            Assert.Single(gate.Lanes).FailingTestNames);
        Assert.Equal(new[] { "     Zeta.First", "     Alpha.Second", "     Beta.Third",
            "     Zeta.First", "     (2 skipped, not failures)" },
            lines.Where(line => line.StartsWith("     ", StringComparison.Ordinal)));
        Assert.DoesNotContain(lines, line => line.Contains("Omitted.", StringComparison.Ordinal));
    }

    [Fact]
    public void RunningConfirmation_FailedLane_ShowsCountAndElapsedUnderThatLane()
    {
        using var fixture = new Fixture();
        var lines = Render(fixture.Load("verification-check"));
        Assert.EndsWith(" | FAILING, confirming 1 failure(s)", lines[0]);
        AssertNested(lines, "confirming: re-running 1 failed test(s), 47s");
        Assert.DoesNotContain(lines, line => line.StartsWith("RUNNING", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("confirming failure:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Failed", false)]
    [InlineData("Failed", true)]
    [InlineData("Error", false)]
    [InlineData("Timeout", false)]
    [InlineData("Passed", false)]
    [InlineData("Passed", true)]
    public void FinishedConfirmation_ConclusiveTrx_ShowsItsResultAndHeader(
        string outcome, bool laneNamedRerun)
    {
        using var fixture = new Fixture();
        fixture.WriteResults(laneNamedRerun ? Target : Focused, "-run-1", (Failure, outcome));
        // The recorded check outcome deliberately disagrees: the displayed result must come from TRX.
        var gate = fixture.Load("check-complete", outcome == "Passed" ? "failed" : "passed");
        foreach (var expanded in new[] { false, true })
        {
            var lines = Render(gate, expanded: expanded);
            Assert.EndsWith(outcome == "Passed" ? " | FLAKE, re-run passed" :
                " | FAILED, 1 confirmed failure(s)", lines[0]);
            AssertNested(lines, outcome == "Passed" ? "not reproduced: passed on re-run (flake)" :
                "confirmed: failed again (real failure)");
            Assert.DoesNotContain(lines, line => line.Contains("confirming failure:", StringComparison.Ordinal));
            Assert.DoesNotContain(lines, line => line.StartsWith("RUNNING", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("empty")]
    [InlineData("NotExecuted")]
    [InlineData("Skipped")]
    [InlineData("Inconclusive")]
    [InlineData("passed-and-skipped")]
    public void FinishedConfirmation_InsufficientTrx_PreservesFailureState(string evidence)
    {
        using var fixture = new Fixture();
        if (evidence == "malformed") File.WriteAllText(fixture.TrxPath(Focused, "-run-1"), "not XML");
        else if (evidence == "empty") fixture.WriteResults(Focused, "-run-1");
        else if (evidence == "passed-and-skipped") fixture.WriteResults(Focused, "-run-1",
            (Failure, "Passed"), ("Sample.Platform.Skip", "NotExecuted"));
        else if (evidence != "missing") fixture.WriteResults(Focused, "-run-1", (Failure, evidence));

        var lines = Render(fixture.Load("check-complete", "passed"));
        Assert.EndsWith(" | FAILING", lines[0]);
        AssertNested(lines, evidence is "missing" or "malformed" or "empty" ?
            "confirmation result unavailable" : "confirmation result inconclusive");
        Assert.DoesNotContain(lines, line => line.Contains("confirmed:", StringComparison.Ordinal) ||
            line.Contains("not reproduced:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(36)]
    [InlineData(80)]
    public void RunningConfirmation_NarrowView_WrapsTheRequiredStatusText(int width)
    {
        using var fixture = new Fixture();
        var lines = Render(fixture.Load("verification-check"), width);
        var header = string.Join(" ", lines.TakeWhile(line => line != "31bcecc5"));
        Assert.Contains("FAILING, confirming 1 failure(s)", header);
        var summary = string.Join(" ", lines.Where(line => line.StartsWith("     ", StringComparison.Ordinal))
            .Select(line => line.Trim()));
        Assert.Contains("confirming: re-running 1 failed test(s), 47s", summary);
        Assert.All(lines, line => Assert.True(line.Length <= width));
        Assert.DoesNotContain("…", summary);
    }

    [Theory]
    [InlineData("verification-check", "Failed")]
    [InlineData("check-complete", "Failed")]
    [InlineData("check-complete", "Passed")]
    public void Confirmation_MtpNamesWithTwoFailedLanes_LinksByResolvedIdentity(
        string phase, string outcome)
    {
        using var fixture = new Fixture();
        const string otherTarget = "infrastructure tests: Other";
        fixture.WriteDefinedFailure(Target, "Sample.Tests.RealFailure", "Fails");
        fixture.WriteDefinedFailure(otherTarget, "Sample.Tests.OtherFailure", "Fails");
        fixture.WriteResults(Focused, "-run-1", (Failure, outcome));

        var gate = fixture.Load(phase, outcome, otherFailedLane: true);
        Assert.Equal(3, gate.Lanes.Count);
        var lane = Assert.Single(gate.Lanes, row => row.Target == Target);
        Assert.Equal(new[] { Failure + "(value: 1)" },
            new OwnerConsoleGateFailureNames().ReadOutcomes(lane).FailureTestNames);
        Assert.Equal(new[] { "RealFailure.Fails" }, lane.FailingTestNames);
        var other = Assert.Single(gate.Lanes, row => row.Target == otherTarget);
        Assert.Equal(new[] { "OtherFailure.Fails" }, other.FailingTestNames);
        Assert.Null(other.Confirmation);
        var confirmation = Assert.IsType<OwnerConsoleGateConfirmation>(lane.Confirmation);
        Assert.Equal(phase == "verification-check", confirmation.Running);
        Assert.Equal(1, confirmation.TargetCount);

        var lines = Render(gate);
        var summary = phase == "verification-check" ? "confirming: re-running 1 failed test(s), 47s" :
            outcome == "Failed" ? "confirmed: failed again (real failure)" :
            "not reproduced: passed on re-run (flake)";
        var laneIndex = Array.FindIndex(lines, line => line.StartsWith(" x Remainder ", StringComparison.Ordinal));
        Assert.True(laneIndex >= 0, "The failed Remainder lane must be visible.");
        Assert.Equal("     RealFailure.Fails", lines[laneIndex + 1]);
        Assert.Equal("     " + summary, lines[laneIndex + 2]);
        Assert.Single(lines, line => line == "     " + summary);
        Assert.EndsWith(phase == "verification-check" ? " | FAILING, confirming 1 failure(s)" :
            outcome == "Failed" ? " | FAILED, 1 confirmed failure(s)" : " | FAILING", lines[0]);
        Assert.DoesNotContain(lines, line => line.StartsWith("RUNNING", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("A friendly display name", StringComparison.Ordinal));
    }

    private static string[] Render(OwnerConsoleGateProgress gate, int width = 200, bool expanded = false) =>
        OwnerConsoleGateLayout.Lines(gate, width, expanded).ToArray();

    private static void AssertNested(string[] lines, string summary)
    {
        var lane = Array.FindIndex(lines, line => line.StartsWith(" x Remainder ", StringComparison.Ordinal));
        Assert.True(lane >= 0, "The failed Remainder lane must be visible.");
        Assert.Equal("     RealFailure.Fails", lines[lane + 1]);
        Assert.Equal("     (6 skipped, not failures)", lines[lane + 2]);
        Assert.Equal("     " + summary, lines[lane + 3]);
        Assert.Single(lines, line => line == "     " + summary);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "gate-confirm-" + Guid.NewGuid().ToString("N"));

        internal Fixture()
        {
            Directory.CreateDirectory(_directory);
            // Put the real failure last, reproducing the previously hidden seventh name.
            WriteResults(Target, "", Enumerable.Range(1, 6)
                .Select(index => ("Sample.Tests.PlatformSkip.Case" + index, "NotExecuted"))
                .Append((Failure, "Failed")).ToArray());
        }

        internal void WriteResults(string target, string suffix, params (string Name, string Outcome)[] results)
        {
            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            new XDocument(new XElement(ns + "TestRun", new XElement(ns + "Results", results.Select(result =>
                new XElement(ns + "UnitTestResult", new XAttribute("testName", result.Name),
                    new XAttribute("outcome", result.Outcome)))),
                new XElement(ns + "ResultSummary", new XElement(ns + "Counters",
                    new XAttribute("total", results.Length),
                    new XAttribute("failed", results.Count(result => result.Outcome == "Failed")),
                    new XAttribute("notExecuted", results.Count(result => result.Outcome == "NotExecuted"))))))
                .Save(TrxPath(target, suffix));
        }

        internal string TrxPath(string target, string suffix) => Path.Combine(_directory,
            AcceptanceArtifactFileNames.BoundFileName(AcceptanceArtifactFileNames.SanitizeFileName(
                Prefix + "." + AcceptanceArtifactFileNames.Slug(target) + suffix), ".trx"));

        internal void WriteDefinedFailure(string target, string className, string methodName)
        {
            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            var testId = Guid.NewGuid().ToString("D");
            const string displayName = "A friendly display name(value: 1)";
            new XDocument(new XElement(ns + "TestRun",
                new XElement(ns + "Results", new XElement(ns + "UnitTestResult",
                    new XAttribute("testId", testId.ToUpperInvariant()),
                    new XAttribute("testName", displayName), new XAttribute("outcome", "Failed"))),
                new XElement(ns + "TestDefinitions", new XElement(ns + "UnitTest",
                    new XAttribute("id", testId), new XAttribute("name", displayName),
                    new XElement(ns + "TestMethod", new XAttribute("className", className),
                        new XAttribute("name", methodName)))))).Save(TrxPath(target, ""));
        }

        internal OwnerConsoleGateProgress Load(string? confirmationPhase = null, string outcome = "failed",
            bool otherFailedLane = false)
        {
            var manifest = Path.Combine(_directory, "manifest.json");
            var log = Path.Combine(_directory, "conduct-events.log");
            File.WriteAllText(manifest, JsonSerializer.Serialize(new
            {
                engine = new { infrastructureTestLanes = (otherFailedLane ? new[] { "Remainder", "Other" } : new[] { "Remainder" })
                    .Select(name => new { name, estimatedSerialSeconds = 804.6 }) }
            }));
            var events = new List<string> { Progress(-60, "check-complete", Target, "remainder", "failed") };
            if (otherFailedLane)
                events.Add(Progress(-59, "check-complete", "infrastructure tests: Other", "other", "failed"));
            if (confirmationPhase is not null)
                events.Add(Progress(-47, confirmationPhase, Focused, "remainder-run-1", outcome));
            File.WriteAllLines(log, events);
            var loader = new OwnerConsoleGateProgressLoader(log, manifest,
                Path.Combine(_directory, "missing-history.jsonl"), new OwnerConsoleTestClock { Now = Now });
            var gate = Assert.IsType<OwnerConsoleGateProgress>(loader.Load(new Dictionary<string, string>()));
            Assert.True(gate.Live);
            return gate;
        }

        private string Progress(int age, string phase, string target, string slug, string outcome) =>
            JsonSerializer.Serialize(new { timestamp = Now.AddSeconds(age), eventKind = "gate-progress", goalId = "31bcecc5",
                detail = $"PHASE_PROGRESS goal=31bcecc5 phase={phase} target=\"{target}\" elapsed_ms=0 outcome={outcome} heartbeat=\"{Path.Combine(_directory, Prefix + "." + slug + ".gate-heartbeat.json")}\"" });

        public void Dispose() => Directory.Delete(_directory, true);
    }
}
