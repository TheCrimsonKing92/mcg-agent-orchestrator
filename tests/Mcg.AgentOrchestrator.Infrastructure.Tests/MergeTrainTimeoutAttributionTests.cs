using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Each test owns its sources and evidence; no shared process or repository state.
public sealed class MergeTrainTimeoutAttributionTests
{
    [Theory]
    [InlineData("Timeout")]
    [InlineData("Error")]
    [InlineData("Aborted")]
    [InlineData("Failed")]
    [InlineData("timeout")]
    public void OwnedFatalRows_ResolveFailedAndReuseReceiptOwnership(string outcome)
    {
        using var fixture = new Fixture();
        var paths = new[] { fixture.Trx("fatal.trx", ("Ns.FirstTests.Fails", outcome, null)),
            fixture.Trx("mixed.trx", ("Ns.FirstTests.Other", "Failed", null),
                ("Ns.UnchangedTests.Passes", "Passed", null), ("Ns.UnchangedTests.Skips", "NotExecuted", null)) };
        Assert.Same(fixture.Members[0], MergeTrainTimeoutAttribution.TryAttribute(paths, fixture.Root, fixture.Members));
        var result = Resolve(fixture, paths);
        Assert.Equal(MergeTrainGateOutcome.Failed, result);
        var receipt = new MergeTrainReceipt("red", MergeTrainIdentity.Create(fixture.Members,
            new string('c', 40), new string('d', 40), "manifest"), result, DateTimeOffset.UnixEpoch, 0, ["gate"], 1, paths);
        Assert.Same(fixture.Members[0], MergeTrainRedAttribution.TryAttribute(receipt, fixture.Root, fixture.Members));
    }

    [Theory]
    [InlineData("unowned")]
    [InlineData("shared")]
    [InlineData("different-members")]
    [InlineData("missing")]
    [InlineData("unreadable")]
    [InlineData("unparseable")]
    [InlineData("apparatus-only")]
    [InlineData("apparatus-mixed")]
    [InlineData("apparatus-stack")]
    [InlineData("Inconclusive")]
    [InlineData("NotRunnable")]
    [InlineData("Unknown")]
    [InlineData("empty")]
    [InlineData("passed-only")]
    [InlineData("skipped-only")]
    [InlineData("counter-mismatch")]
    [InlineData("counter-malformed")]
    [InlineData("fatal-counter-mismatch")]
    [InlineData("missing-counters")]
    [InlineData("wrong-namespace")]
    public void IneligibleEvidence_RetainsInfrastructureFailure(string scenario)
    {
        using var fixture = new Fixture();
        const string apparatus = "DotnetBuildSlotsBusyException: Stable dotnet build slots busy";
        var control = fixture.Trx("control.trx", ("Ns.FirstTests.Fails", "Timeout", null));
        Assert.Equal(MergeTrainGateOutcome.Failed, Resolve(fixture, [control]));
        var name = scenario == "unowned" ? "Ns.UnchangedTests.Fails" : "Ns.FirstTests.Fails";
        var path = fixture.Trx("timeout.trx", (name, "Timeout", scenario == "apparatus-only" ? apparatus : null));
        IReadOnlyList<string> paths = [path];
        IReadOnlyList<MergeTrainMemberBinding> members = fixture.Members;
        FileStream? locked = null;
        try
        {
            switch (scenario)
            {
                case "shared":
                    members = [fixture.Members[0], fixture.Members[1] with { LandingPaths = ["tests/First.cs"] }];
                    break;
                case "different-members":
                case "apparatus-mixed":
                    paths = [path, fixture.Trx("other.trx", ("Ns.SecondTests.Fails", "Timeout",
                        scenario == "apparatus-mixed" ? apparatus : null))];
                    break;
                case "missing":
                    paths = [path, Path.Combine(fixture.Root, "missing.trx")];
                    Assert.Equal(AcceptanceTrxReadStatus.Missing, AcceptanceTrxFailureReader.Read(paths[1]).Status);
                    break;
                case "unreadable":
                    // Keep an owned, readable failure alongside the incomplete evidence.
                    var lockedPath = fixture.Trx("locked.trx", ("Ns.FirstTests.Other", "Timeout", null));
                    paths = [path, lockedPath];
                    locked = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    Assert.Equal(AcceptanceTrxReadStatus.Unreadable, AcceptanceTrxFailureReader.Read(lockedPath).Status);
                    break;
                case "unparseable":
                    var malformed = Path.Combine(fixture.Root, "malformed.trx");
                    File.WriteAllText(malformed, "<TestRun");
                    paths = [path, malformed];
                    Assert.Equal(AcceptanceTrxReadStatus.Unparseable, AcceptanceTrxFailureReader.Read(malformed).Status);
                    break;
                case "Inconclusive":
                case "NotRunnable":
                case "Unknown":
                    paths = [path, fixture.Trx("unknown.trx", ("Ns.FirstTests.Other", scenario, null))];
                    break;
                case "empty": paths = []; break;
                case "passed-only": paths = [fixture.Trx("pass.trx", (name, "Passed", null))]; break;
                case "skipped-only": paths = [fixture.Trx("skip.trx", (name, "NotExecuted", null))]; break;
                case "apparatus-stack":
                case "counter-mismatch":
                case "counter-malformed":
                case "fatal-counter-mismatch":
                case "missing-counters":
                case "wrong-namespace":
                    var document = XDocument.Load(path);
                    var counters = document.Descendants().Single(element => element.Name.LocalName == "Counters");
                    if (scenario == "apparatus-stack")
                        document.Descendants().Single(element => element.Name.LocalName == "UnitTestResult").Add(
                            new XElement(Fixture.Ns + "Output", new XElement(Fixture.Ns + "ErrorInfo",
                                new XElement(Fixture.Ns + "StackTrace", apparatus))));
                    else if (scenario == "missing-counters") counters.Remove();
                    else if (scenario == "wrong-namespace")
                    {
                        document.Root!.SetAttributeValue("xmlns", null);
                        document.Root.Name = "TestRun";
                    }
                    else counters.SetAttributeValue(scenario == "fatal-counter-mismatch" ? "timeout" : "executed",
                        scenario == "counter-malformed" ? "bad" : "2");
                    document.Save(path);
                    if (scenario == "wrong-namespace")
                        Assert.Equal(XNamespace.None + "TestRun", XDocument.Load(path).Root!.Name);
                    break;
            }
            Assert.Null(MergeTrainTimeoutAttribution.TryAttribute(paths, fixture.Root, members));
            Assert.Equal(MergeTrainGateOutcome.InfrastructureFailure, MergeTrainTimeoutAttribution.ResolveGateOutcome(
                new AcceptanceCohortGateClassification(AcceptanceCohortGateOutcome.InfrastructureFailure,
                    AcceptanceCohortInfrastructureReasonCodes.TrxEvidenceIncoherent), paths, fixture.Root, members));
        }
        finally { locked?.Dispose(); }
    }

    [Theory]
    [InlineData(AcceptanceCohortInfrastructureReasonCodes.ExitCodeMissing)]
    [InlineData(AcceptanceCohortInfrastructureReasonCodes.VerificationSkipped)]
    [InlineData(AcceptanceCohortInfrastructureReasonCodes.IoFailure)]
    [InlineData(null)]
    public void OtherInfrastructureReason_LeavesOwnedTimeoutAsInfrastructure(string? reason)
    {
        using var fixture = new Fixture();
        var paths = new[] { fixture.Trx("timeout.trx", ("Ns.FirstTests.Fails", "Timeout", null)) };
        Assert.Same(fixture.Members[0], MergeTrainTimeoutAttribution.TryAttribute(paths, fixture.Root, fixture.Members));
        Assert.Equal(MergeTrainGateOutcome.InfrastructureFailure, Resolve(fixture, paths, reason));
    }

    [Theory]
    [InlineData(AcceptanceCohortGateOutcome.Passed, MergeTrainGateOutcome.Passed)]
    [InlineData(AcceptanceCohortGateOutcome.Failed, MergeTrainGateOutcome.Failed)]
    public void OrdinaryVerdicts_KeepTheirOutcome(AcceptanceCohortGateOutcome original, MergeTrainGateOutcome expected)
    {
        using var fixture = new Fixture();
        Assert.Equal(expected, MergeTrainTimeoutAttribution.ResolveGateOutcome(
            new AcceptanceCohortGateClassification(original), [], fixture.Root, fixture.Members));
    }

    private static MergeTrainGateOutcome Resolve(Fixture fixture, IReadOnlyList<string> paths,
        string? reason = AcceptanceCohortInfrastructureReasonCodes.TrxEvidenceIncoherent) =>
        MergeTrainTimeoutAttribution.ResolveGateOutcome(new AcceptanceCohortGateClassification(
            AcceptanceCohortGateOutcome.InfrastructureFailure, reason), paths, fixture.Root, fixture.Members);

    private sealed class Fixture : IDisposable
    {
        internal static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"train-timeout-{Guid.NewGuid():N}");
        internal MergeTrainMemberBinding[] Members { get; } = [Member('a', "tests/First.cs"), Member('b', "tests/Second.cs")];

        internal Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Root, "tests"));
            File.WriteAllText(Path.Combine(Root, "tests/First.cs"), "namespace Ns; public class FirstTests { }");
            File.WriteAllText(Path.Combine(Root, "tests/Second.cs"), "namespace Ns; public class SecondTests { }");
            File.WriteAllText(Path.Combine(Root, "tests/Unchanged.cs"), "namespace Ns; public class UnchangedTests { }");
        }

        internal string Trx(string file, params (string Name, string Outcome, string? Message)[] rows)
        {
            var path = Path.Combine(Root, file);
            var counters = new XElement(Ns + "Counters", new XAttribute("total", rows.Length),
                new XAttribute("executed", rows.Count(row => row.Outcome != "NotExecuted")));
            foreach (var outcome in new[] { "Passed", "Failed", "Timeout", "Error", "Aborted" })
                counters.SetAttributeValue(outcome.ToLowerInvariant(), rows.Count(row => row.Outcome.Equals(outcome, StringComparison.OrdinalIgnoreCase)));
            new XDocument(new XElement(Ns + "TestRun", new XElement(Ns + "Results", rows.Select((row, index) =>
                new XElement(Ns + "UnitTestResult", new XAttribute("testId", index), new XAttribute("testName", row.Name),
                    new XAttribute("outcome", row.Outcome), row.Message is null ? null :
                        new XElement(Ns + "Output", new XElement(Ns + "ErrorInfo", new XElement(Ns + "Message", row.Message)))))),
                new XElement(Ns + "ResultSummary", counters))).Save(path);
            return path;
        }

        private static MergeTrainMemberBinding Member(char id, string path) => new(new GoalId(new string(id, 32)),
            new string(id, 40), new string(id, 40), [path], [$"member:{id}"], ChangeRiskTier.Behavior,
            ConductorTransitionDecision.Auto, GateReadyMergeStatus.Clean.ToString(),
            GateReadyMergeReason.NoConflictsDetected.ToString(), new string(id, 40));

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
