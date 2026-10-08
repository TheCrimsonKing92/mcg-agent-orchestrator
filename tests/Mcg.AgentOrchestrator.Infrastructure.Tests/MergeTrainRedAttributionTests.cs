using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Each case owns its source tree and TRX artifacts; no repository or shared state is mutated.
public sealed class MergeTrainRedAttributionTests
{
    private const string LaneGuard = "AcceptanceGateEngineSettingsTests.AcceptanceGateEngineDisabledCollectionsSpanningLanesShareAnExclusiveResource";
    private const string DecisionGuard = "WorkflowDecisionCoverageRatchetTests.EveryUndecidedSite_IsAllowListedByFileAndMember";

    private static string LaneMessage(string subject) =>
        "Disabled collection 'DotnetBuildEnvironmentManagerStaticHooks' spans acceptance lanes " +
        "[Remainder, Dotnet build slots] without a shared exclusive resource key. " +
        $"Mapped classes: [{subject} -> Remainder, DotnetBuildEnvironmentManagerTests -> Dotnet build slots].";

    [Fact]
    public void GuardSubjects_OverrideGuardSourceOwnershipAndAttributeOneMember()
    {
        using var fixture = new Fixture();
        fixture.Source("tests/First.cs", "DotnetBuildEnvironmentManagerStableSlotHolderLabelTests");
        // The replayed decision guard itself was owned by an innocent train member.
        fixture.Source("tests/Second.cs", "AcceptanceGateEngineSettingsTests", "WorkflowDecisionCoverageRatchetTests");
        var receipt = fixture.GuardReceipt(
            (LaneGuard, LaneMessage("Ns.DotnetBuildEnvironmentManagerStableSlotHolderLabelTests")),
            (DecisionGuard, "Unlisted undecided sites:\nFirst.cs : DeferredRun"));
        Assert.Same(fixture.Members[0], MergeTrainRedAttribution.TryAttribute(
            receipt, fixture.Root, fixture.Members, out var subjects));
        Assert.Equal(new[] { "First.cs", "Ns.DotnetBuildEnvironmentManagerStableSlotHolderLabelTests" }, subjects);
    }

    [Theory]
    [InlineData(false, "missing")]
    [InlineData(true, "missing")]
    [InlineData(false, "shared")]
    [InlineData(true, "shared")]
    [InlineData(false, "different-members")]
    [InlineData(true, "different-members")]
    [InlineData(false, "malformed-message")]
    [InlineData(true, "malformed-message")]
    [InlineData(false, "missing-trx")]
    [InlineData(true, "missing-trx")]
    [InlineData(false, "unreadable-trx")]
    [InlineData(true, "unreadable-trx")]
    [InlineData(false, "apparatus")]
    [InlineData(true, "apparatus")]
    public void GuardSubjects_IncompleteOrAmbiguousEvidenceRefusesAttribution(bool file, string scenario)
    {
        using var fixture = new Fixture();
        fixture.Source("tests/First.cs", "FirstTests");
        fixture.Source("tests/Second.cs", "SecondTests", "AcceptanceGateEngineSettingsTests", "WorkflowDecisionCoverageRatchetTests");
        var subject = scenario == "missing" ? (file ? "Missing.cs" : "Ns.MissingTests") :
            file ? "First.cs" : "Ns.FirstTests";
        var message = file ? "Unlisted undecided sites:\n" + subject + " : DeferredRun" : LaneMessage(subject);
        if (scenario == "different-members")
            message = file ? message + "\nSecond.cs : Other" :
                LaneMessage("Ns.FirstTests -> Remainder, Ns.SecondTests");
        if (scenario == "malformed-message") message = "Unknown guard failure involving " + subject;
        if (scenario == "apparatus") message = "DotnetBuildSlotsBusyException: Stable dotnet build slots busy\n" + message;
        var receipt = fixture.GuardReceipt((file ? DecisionGuard : LaneGuard, message));
        var members = scenario == "shared"
            ? new[] { fixture.Members[0], fixture.Members[1] with { LandingPaths = ["tests/First.cs", "tests/Second.cs"] } }
            : fixture.Members;
        if (scenario == "missing-trx")
            receipt = receipt with { GateTestResultPaths = [.. receipt.GateTestResultPaths, Path.Combine(fixture.Root, "missing.trx")] };
        using var lockedFile = scenario == "unreadable-trx"
            ? new FileStream(receipt.GateTestResultPaths[0], FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        Assert.Null(MergeTrainRedAttribution.TryAttribute(receipt, fixture.Root, members, out var subjects));
        Assert.Empty(subjects);
    }

    [Fact]
    public void GuardAndOrdinaryFailures_DivergentOwnersRefuseAttribution()
    {
        using var fixture = new Fixture();
        fixture.Source("tests/First.cs", "FirstTests");
        fixture.Source("tests/Second.cs", "SecondTests");
        var receipt = fixture.GuardReceipt((DecisionGuard, "Unlisted undecided sites:\nFirst.cs : DeferredRun"),
            ("Ns.SecondTests.Fails", "ordinary failure"));
        Assert.Null(MergeTrainRedAttribution.TryAttribute(receipt, fixture.Root, fixture.Members));
    }

    [Fact]
    public void FileSubject_DuplicateBasenamesAcrossMembersRefuseAttribution()
    {
        using var fixture = new Fixture();
        var members = new[] { fixture.Members[0], fixture.Members[1] with { LandingPaths = ["src/First.cs"] } };
        Assert.Null(MergeTrainRedAttribution.TryAttribute(fixture.GuardReceipt(
            (DecisionGuard, "Unlisted undecided sites:\nFirst.cs : DeferredRun")), fixture.Root, members));
        Assert.Same(members[0], MergeTrainRedAttribution.TryAttribute(fixture.GuardReceipt(
            (DecisionGuard, "Unlisted undecided sites:\ntests/First.cs : DeferredRun")), fixture.Root, members));
    }

    [Fact]
    public void UnrecognizedTest_WithGuardShapedMessageUsesItsOwnSource()
    {
        using var fixture = new Fixture();
        fixture.Source("tests/First.cs", "FirstTests");
        fixture.Source("tests/Second.cs", "SecondTests");
        Assert.Same(fixture.Members[1], MergeTrainRedAttribution.TryAttribute(fixture.GuardReceipt(
            ("Ns.SecondTests.Fails", "Unlisted undecided sites:\nFirst.cs : DeferredRun")), fixture.Root, fixture.Members));
    }

    [Theory]
    [InlineData("unresolved")]
    [InlineData("unchanged")]
    [InlineData("shared")]
    [InlineData("different-members")]
    public void AmbiguousOwnership_ReturnsNoAttribution(string scenario)
    {
        using var fixture = new Fixture();
        fixture.Source("tests/First.cs", "FirstTests", "SharedTests");
        fixture.Source("tests/Second.cs", "SecondTests", "SharedTests");
        fixture.Source("tests/Unchanged.cs", "UnchangedTests");
        var names = scenario switch
        {
            "unresolved" => new[] { "Ns.MissingTests.Fails" },
            "unchanged" => ["Ns.UnchangedTests.Fails"],
            "shared" => ["Ns.SharedTests.Fails"],
            "different-members" => ["Ns.FirstTests.Fails", "Ns.SecondTests.Fails"],
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        var receipt = fixture.Receipt(names);
        Assert.Equal(names, MergeTrainRedAttribution.ReadFatalFailures(receipt.GateTestResultPaths)
            .Select(failure => failure.TestName));
        Assert.Null(MergeTrainRedAttribution.TryAttribute(receipt, fixture.Root, fixture.Members));
    }

    [Fact]
    public void SharedLandingPath_ReturnsNoAttribution()
    {
        using var fixture = new Fixture();
        fixture.Source("tests/First.cs", "FirstTests");
        var members = new[] { fixture.Members[0], fixture.Members[1] with { LandingPaths = ["tests/First.cs"] } };
        Assert.Null(MergeTrainRedAttribution.TryAttribute(
            fixture.Receipt("Ns.FirstTests.Fails"), fixture.Root, members));
    }

    [Fact]
    public void OneOwnerAcrossFailures_NormalizesPathsAndAllowsUnchangedPartialFiles()
    {
        using var fixture = new Fixture();
        fixture.Source("tests/First.cs", "FirstTests", "AnotherTests");
        fixture.Source("tests/Unchanged.cs", "FirstTests");
        var members = new[] { fixture.Members[0] with { LandingPaths = [" ./TESTS\\FIRST.cs "] }, fixture.Members[1] };
        var receipt = fixture.Receipt("Ns.FirstTests.Fails", "Ns.FirstTests.Other", "Ns.AnotherTests.Fails");
        Assert.Same(members[0], MergeTrainRedAttribution.TryAttribute(receipt, fixture.Root, members));
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Error")]
    [InlineData("Timeout")]
    [InlineData("Aborted")]
    [InlineData("NotRunnable")]
    public void FatalOutcome_AttributesThroughSharedReader(string outcome)
    {
        using var fixture = new Fixture();
        fixture.Source("tests/First.cs", "FirstTests");
        var receipt = fixture.Receipt("Ns.FirstTests.Fails");
        fixture.RewriteOutcome(receipt, outcome);
        Assert.Equal(outcome, Assert.Single(MergeTrainRedAttribution.ReadFatalFailures(receipt.GateTestResultPaths)).Outcome);
        Assert.Same(fixture.Members[0], MergeTrainRedAttribution.TryAttribute(receipt, fixture.Root, fixture.Members));
    }

    [Theory]
    [InlineData("NotExecuted")]
    [InlineData("apparatus")]
    [InlineData("size-only")]
    [InlineData("infrastructure")]
    [InlineData("passed")]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("unreadable")]
    public void IneligibleEvidence_ReturnsNoAttribution(string scenario)
    {
        using var fixture = new Fixture();
        fixture.Source("tests/First.cs", "FirstTests");
        var receipt = fixture.Receipt("Ns.FirstTests.Fails");
        FileStream? lockedFile = null;
        try
        {
            switch (scenario)
            {
                case "NotExecuted":
                    fixture.RewriteOutcome(receipt, scenario);
                    break;
                case "apparatus":
                    fixture.AddApparatusFailure(receipt, replace: true);
                    break;
                case "size-only":
                    receipt = receipt with { GateTestResultPaths = [], FailedChecks = [SourceSizeRatchetPreflight.CheckName] };
                    break;
                case "infrastructure":
                    receipt = receipt with { Outcome = MergeTrainGateOutcome.InfrastructureFailure };
                    break;
                case "passed":
                    receipt = receipt with { Outcome = MergeTrainGateOutcome.Passed };
                    break;
                case "missing":
                    // A valid failure alongside missing evidence must not attribute the partial read.
                    receipt = receipt with { GateTestResultPaths = [.. receipt.GateTestResultPaths, Path.Combine(fixture.Root, "absent.trx")] };
                    break;
                case "malformed":
                    File.WriteAllText(receipt.GateTestResultPaths[0], "<TestRun");
                    break;
                case "unreadable":
                    lockedFile = new FileStream(receipt.GateTestResultPaths[0], FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    Assert.Equal(AcceptanceTrxReadStatus.Unreadable, AcceptanceTrxFailureReader.Read(receipt.GateTestResultPaths[0]).Status);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario));
            }
            Assert.Null(MergeTrainRedAttribution.TryAttribute(receipt, fixture.Root, fixture.Members));
        }
        finally { lockedFile?.Dispose(); }
    }

    [Fact]
    public void ApparatusAlongsideFatalFailure_AttributesOnlyTheFatalFailure()
    {
        using var fixture = new Fixture();
        fixture.Source("tests/First.cs", "FirstTests");
        fixture.Source("tests/Second.cs", "SecondTests");
        var receipt = fixture.Receipt("Ns.FirstTests.Fails");
        fixture.AddApparatusFailure(receipt, replace: false);
        Assert.Equal("Ns.FirstTests.Fails", Assert.Single(
            MergeTrainRedAttribution.ReadFatalFailures(receipt.GateTestResultPaths)).TestName);
        Assert.Same(fixture.Members[0], MergeTrainRedAttribution.TryAttribute(receipt, fixture.Root, fixture.Members));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"train-red-{Guid.NewGuid():N}");
        internal MergeTrainMemberBinding[] Members { get; } = [Member('a', "tests/First.cs"), Member('b', "tests/Second.cs")];

        internal void Source(string path, params string[] classes)
        {
            var absolute = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            File.WriteAllText(absolute, "namespace Ns; " + string.Join(" ", classes.Select(name => $"public partial class {name} {{ }}")));
        }

        internal MergeTrainReceipt Receipt(params string[] names)
        {
            Directory.CreateDirectory(Root);
            var trx = Path.Combine(Root, "red.trx");
            new XDocument(new XElement("TestRun", new XElement("Results", names.Select((name, index) =>
                new XElement("UnitTestResult", new XAttribute("testId", index), new XAttribute("testName", name),
                    new XAttribute("outcome", "Failed")))))).Save(trx);
            var identity = MergeTrainIdentity.Create(Members, new string('c', 40), new string('d', 40), "manifest");
            return new MergeTrainReceipt("red", identity, MergeTrainGateOutcome.Failed,
                DateTimeOffset.UnixEpoch, 0, ["gate"], 1, [trx]);
        }

        internal MergeTrainReceipt GuardReceipt(params (string Name, string Message)[] failures)
        {
            var receipt = Receipt(failures.Select(failure => failure.Name).ToArray());
            var document = XDocument.Load(receipt.GateTestResultPaths[0]);
            foreach (var (result, failure) in document.Descendants("UnitTestResult").Zip(failures))
                result.Add(new XElement("Output", new XElement("ErrorInfo", new XElement("Message", failure.Message))));
            document.Save(receipt.GateTestResultPaths[0]);
            return receipt;
        }

        internal void RewriteOutcome(MergeTrainReceipt receipt, string outcome)
        {
            var path = receipt.GateTestResultPaths[0];
            var document = XDocument.Load(path);
            document.Descendants("UnitTestResult").Single().SetAttributeValue("outcome", outcome);
            document.Save(path);
        }

        internal void AddApparatusFailure(MergeTrainReceipt receipt, bool replace)
        {
            var path = receipt.GateTestResultPaths[0];
            var document = XDocument.Load(path);
            var results = document.Descendants("Results").Single();
            if (replace) results.RemoveNodes();
            results.Add(new XElement("UnitTestResult", new XAttribute("testName", "Ns.SecondTests.Fails"),
                new XAttribute("outcome", "Failed"), new XElement("Output", new XElement("ErrorInfo",
                    new XElement("Message", "DotnetBuildSlotsBusyException: Stable dotnet build slots busy")))));
            document.Save(path);
        }

        private static MergeTrainMemberBinding Member(char id, string path) => new(new GoalId(new string(id, 32)),
            new string(id, 40), new string(id, 40), [path], [$"member:{id}"], ChangeRiskTier.Behavior,
            ConductorTransitionDecision.Auto, GateReadyMergeStatus.Clean.ToString(),
            GateReadyMergeReason.NoConflictsDetected.ToString(), new string(id, 40));

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
