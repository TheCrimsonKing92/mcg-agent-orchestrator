using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Fixture = RepositoryTestReachTests.RepositoryFixture;

// Parallel-safe: each case owns its workspace, landing files and TRX artifacts.
public sealed class MergeTrainPathAttributionTests
{
    private static readonly string[] Paths =
    [
        "tests/Mcg.AgentOrchestrator.Core.Tests/OldestCulpritTests.cs",
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/OtherMemberTests.cs",
        "tests/Mcg.AgentOrchestrator.Dashboard.Tests/NewestMemberTests.cs"
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnchangedGuard_NamingExistingLandingPath_AttributesItsMember(bool stackOnly)
    {
        using var fixture = CreateFixture();
        var members = Members();
        var absolute = Path.Combine(fixture.Root, Paths[1]).Replace('/', '\\');
        var results = Trx(fixture, new Failure("Ns.GuardTests.Fails",
            stackOnly ? null : $"Unproven occupancy identity: Ns.X at {Paths[1]}:70: x.",
            stackOnly ? $"at Ns.GuardTests.Fails() in {absolute}:line 70" : null));

        Assert.Same(members[1], MergeTrainPathAttribution.TryAttribute(results, fixture.Root, members, out var evidence));
        Assert.Equal(new[] { "Ns.GuardTests@" + Paths[1] }, evidence);
    }

    [Fact]
    public void RepeatedPathAcrossClasses_DeduplicatesAndOrdersQualifiedEvidence()
    {
        using var fixture = CreateFixture();
        var members = Members();
        var message = $"'{Paths[1]}' and {Paths[1]}:70";
        var results = Trx(fixture, new Failure("Ns.ZebraTests.Fails", message),
            new("Ns.AlphaTests.Fails", message), new("Ns.AlphaTests.FailsAgain", message));

        Assert.Same(members[1], MergeTrainPathAttribution.TryAttribute(results, fixture.Root, members, out var evidence));
        Assert.Equal(new[] { "Ns.AlphaTests@" + Paths[1], "Ns.ZebraTests@" + Paths[1] }, evidence);
    }

    [Theory]
    [InlineData("shared")]
    [InlineData("unnamed-second")]
    [InlineData("two-members")]
    [InlineData("removed")]
    [InlineData("bak")]
    [InlineData("prefix-name")]
    [InlineData("prefix-char")]
    [InlineData("no-slash")]
    [InlineData("guard")]
    [InlineData("unparseable")]
    [InlineData("missing-trx")]
    [InlineData("apparatus")]
    [InlineData("empty-class")]
    [InlineData("non-fatal")]
    [InlineData("suffix-collision")]
    public void AmbiguousOrIncompleteEvidence_RefusesWithoutEvidence(string scenario)
    {
        using var fixture = CreateFixture();
        var members = Members();
        var failures = new List<Failure> { new("Ns.GuardTests.Fails", $"at {Paths[1]}:70") };
        switch (scenario)
        {
            case "shared": members[2] = Member('c', Paths[2], Paths[1]); break;
            case "unnamed-second": failures.Add(new("Ns.SecondTests.Fails", "Assert.Equal failed")); break;
            case "two-members": failures.Add(new("Ns.SecondTests.Fails", $"at {Paths[2]}:70")); break;
            case "removed": File.Delete(Path.Combine(fixture.Root, Paths[1])); break;
            case "bak": failures[0] = new("Ns.GuardTests.Fails", Paths[1] + ".bak"); break;
            case "prefix-name":
                failures[0] = new("Ns.GuardTests.Fails", Paths[1].Replace("/Other", "/MyOther"));
                break;
            case "prefix-char": failures[0] = new("Ns.GuardTests.Fails", "X" + Paths[1]); break;
            case "no-slash":
                fixture.Write("OtherMemberTests.cs", "public class OtherMemberTests { }");
                members[1] = Member('b', "OtherMemberTests.cs");
                failures[0] = new("Ns.GuardTests.Fails", "OtherMemberTests.cs:70");
                break;
            case "guard":
                failures.Add(new("Ns.AcceptanceGateEngineSettingsTests.AcceptanceGateEngineDisabledCollectionsSpanningLanesShareAnExclusiveResource",
                    Paths[1]));
                break;
            case "apparatus":
                failures[0] = new("Ns.GuardTests.Fails", "DotnetBuildSlotsBusyException: Stable dotnet build slots busy at " + Paths[1]);
                break;
            case "empty-class": failures[0] = new("Fails", Paths[1]); break;
            case "non-fatal": failures[0] = new("Ns.GuardTests.Fails", Paths[1], Outcome: "Passed"); break;
            case "suffix-collision":
                var suffix = "Mcg.AgentOrchestrator.Infrastructure.Tests/OtherMemberTests.cs";
                fixture.Write(suffix, "public class OtherMemberTests { }");
                members[2] = Member('c', suffix);
                break;
        }
        var results = Trx(fixture, failures.ToArray());
        if (scenario == "unparseable")
        {
            fixture.Write("broken.trx", "<TestRun");
            results = [.. results, Path.Combine(fixture.Root, "broken.trx")];
        }
        if (scenario == "missing-trx") results = [.. results, Path.Combine(fixture.Root, "missing.trx")];

        Assert.Null(MergeTrainPathAttribution.TryAttribute(results, fixture.Root, members, out var evidence));
        Assert.Empty(evidence);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("'", "'")]
    [InlineData("/", ":line 70")]
    [InlineData("(", ")")]
    public void ExactPathBoundaries_AndCaseInsensitiveText_AreAccepted(string before, string after)
    {
        using var fixture = CreateFixture();
        var members = Members();
        var results = Trx(fixture, new Failure("Ns.GuardTests.Fails", before + Paths[1].ToUpperInvariant() + after));
        Assert.Same(members[1], MergeTrainPathAttribution.TryAttribute(results, fixture.Root, members, out var evidence));
        Assert.Equal(new[] { "Ns.GuardTests@" + Paths[1] }, evidence);
    }

    [Theory]
    [InlineData("A")]
    [InlineData("z")]
    [InlineData("0")]
    [InlineData(".")]
    [InlineData("_")]
    [InlineData("-")]
    [InlineData("/")]
    public void ForbiddenBoundaryCharacter_RefusesSubstringMatches(string character)
    {
        using var fixture = CreateFixture();
        var members = Members();
        var results = Trx(fixture, new Failure("Ns.GuardTests.Fails", Paths[1] + character));
        Assert.Null(MergeTrainPathAttribution.TryAttribute(results, fixture.Root, members, out var evidence));
        Assert.Empty(evidence);
        if (character == "/") return; // A slash is allowed before the path, as in absolute paths.
        results = Trx(fixture, new Failure("Ns.GuardTests.Fails", character + Paths[1]));
        Assert.Null(MergeTrainPathAttribution.TryAttribute(results, fixture.Root, members, out evidence));
        Assert.Empty(evidence);
    }

    [Fact]
    public void ValidOccurrenceAfterRejectedOccurrence_StillAttributes()
    {
        using var fixture = CreateFixture();
        var members = Members();
        var results = Trx(fixture, new Failure("Ns.GuardTests.Fails", Paths[1] + ".bak then " + Paths[1]));
        Assert.Same(members[1], MergeTrainPathAttribution.TryAttribute(results, fixture.Root, members, out var evidence));
        Assert.Equal(new[] { "Ns.GuardTests@" + Paths[1] }, evidence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LandingPaths_AreNormalizedBeforeMatching(bool rooted)
    {
        using var fixture = CreateFixture();
        var members = Members();
        members[1] = Member('b', rooted ? Path.Combine(fixture.Root, Paths[1]) : " ././" + Paths[1].Replace('/', '\\') + " ");
        var results = Trx(fixture, new Failure("Ns.GuardTests.Fails", Paths[1]));
        Assert.Same(members[1], MergeTrainPathAttribution.TryAttribute(results, fixture.Root, members, out var evidence));
        Assert.Equal(new[] { "Ns.GuardTests@" + Paths[1] }, evidence);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public void Fallback_UsesPathLastOnAnyAttempt_OrPreservesUnattributedOutputs(int attempt, bool namesPath)
    {
        using var fixture = CreateFixture();
        var members = Members();
        var results = Trx(fixture, new Failure("Ns.GuardTests.Fails", namesPath ? Paths[1] : "Assert.Equal failed"));
        Assert.Null(MergeTrainImpactAttribution.TryAttribute(results, fixture.Root, members, out _));
        Assert.Null(MergeTrainReachAttribution.TryAttribute(results, fixture.Root, members, out _));

        var attributed = MergeTrainFallbackAttribution.TryAttribute(results, fixture.Root, members, attempt,
            out var subjects, out var source);
        if (namesPath)
        {
            Assert.Same(members[1], attributed);
            Assert.Equal("path", source);
            Assert.Equal(new[] { "Ns.GuardTests@" + Paths[1] }, subjects);
        }
        else
        {
            Assert.Null(attributed);
            Assert.Empty(subjects);
            Assert.Equal("ownership", source);
        }
    }

    private static Fixture CreateFixture()
    {
        var fixture = new Fixture();
        foreach (var path in Paths)
            fixture.Write(path, $"namespace Ns; public class {Path.GetFileNameWithoutExtension(path)} {{ }}");
        fixture.Write("tests/UnchangedTests.cs", "namespace Ns; public class GuardTests { }");
        return fixture;
    }

    private static MergeTrainMemberBinding[] Members() =>
        [Member('a', Paths[0]), Member('b', Paths[1]), Member('c', Paths[2])];

    private static MergeTrainMemberBinding Member(char id, params string[] paths) => new(new GoalId(new string(id, 32)),
        new string(id, 40), new string(id, 40), paths, [$"member:{id}"], ChangeRiskTier.Behavior,
        ConductorTransitionDecision.Auto, GateReadyMergeStatus.Clean.ToString(), GateReadyMergeReason.NoConflictsDetected.ToString());

    private static string[] Trx(Fixture fixture, params Failure[] failures)
    {
        var path = Path.Combine(fixture.Root, "red.trx");
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        new XDocument(new XElement(ns + "TestRun", new XElement(ns + "Results", failures.Select(failure =>
            new XElement(ns + "UnitTestResult", new XAttribute("testName", failure.Name), new XAttribute("outcome", failure.Outcome),
                new XElement(ns + "Output", new XElement(ns + "ErrorInfo",
                    new XElement(ns + "Message", failure.Message), new XElement(ns + "StackTrace", failure.Stack)))))),
            new XElement(ns + "ResultSummary", new XAttribute("outcome", "Failed"), new XElement(ns + "Counters",
                new XAttribute("total", failures.Length), new XAttribute("executed", failures.Length),
                new XAttribute("passed", failures.Count(failure => failure.Outcome == "Passed")),
                new XAttribute("failed", failures.Count(failure => failure.Outcome == "Failed")))))).Save(path);
        var read = AcceptanceTrxFailureReader.Read(path);
        Assert.Equal(AcceptanceTrxReadStatus.Readable, read.Status);
        Assert.Equal(failures.Count(failure => failure.Outcome != "Passed"), read.Failures.Count);
        foreach (var failure in read.Failures)
        {
            Assert.Equal(failure.Outcome == "Failed", AcceptanceTrxOutcomeTaxonomy.IsFatal(failure.Outcome));
            if (failure.Message?.StartsWith("DotnetBuildSlotsBusyException:", StringComparison.Ordinal) == true)
                Assert.NotNull(ApparatusInfrastructureSignatures.Match(failure.Message, failure.StackTrace));
            else Assert.Null(ApparatusInfrastructureSignatures.Match(failure.Message, failure.StackTrace));
        }
        return [path];
    }

    private sealed record Failure(string Name, string? Message, string? Stack = null, string Outcome = "Failed");
}
