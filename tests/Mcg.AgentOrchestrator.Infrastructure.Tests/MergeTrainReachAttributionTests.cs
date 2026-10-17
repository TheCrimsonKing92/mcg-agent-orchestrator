using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Fixture = RepositoryTestReachTests.RepositoryFixture;

// Parallel-safe: every case owns its project tree and failure artifacts.
public sealed class MergeTrainReachAttributionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WholeProjectPlans_UniqueReachAttributesAllClassesInSortedOrder(bool sourcePeer)
    {
        using var fixture = CreateFixture();
        var members = new[] { Member('a', Fixture.Source), Member('b', sourcePeer ? Fixture.Other : "docs/b.md"), Member('c') };
        Assert.Contains(RepositoryTestImpactPlanner.Plan(members[0].LandingPaths, fixture.Root).Checks,
            check => check.TestProject == RepositoryTestProject.Infrastructure && check.TestClassSelections is null);
        if (sourcePeer)
        {
            Assert.Contains(RepositoryTestImpactPlanner.Plan(members[1].LandingPaths, fixture.Root).Checks,
                check => check.TestProject == RepositoryTestProject.Infrastructure && check.TestClassSelections is null);
            Assert.Null(MergeTrainImpactAttribution.TryAttribute(Trx(fixture.Root, "ZebraTests.Fails", "AlphaTests.Fails"),
                fixture.Root, members, out _));
        }
        Assert.Same(members[0], MergeTrainReachAttribution.TryAttribute(
            Trx(fixture.Root, "ZebraTests.Fails", "AlphaTests.Fails", "AlphaTests.Fails"), fixture.Root, members, out var classes));
        Assert.Equal(new[] { "AlphaTests", "ZebraTests" }, classes);
    }

    [Theory]
    [InlineData("shared")]
    [InlineData("unreached")]
    [InlineData("degraded")]
    [InlineData("unreadable")]
    [InlineData("apparatus")]
    [InlineData("guard")]
    [InlineData("split-owners")]
    [InlineData("deleted-source")]
    public void AmbiguousOrIncompleteEvidence_ReturnsNullWithoutClasses(string scenario)
    {
        using var fixture = CreateFixture();
        var members = new[] { Member('a', Fixture.Source), Member('b', Fixture.Other), Member('c') };
        var paths = Trx(fixture.Root, "AlphaTests.Fails", "ZebraTests.Fails");
        switch (scenario)
        {
            case "shared":
                fixture.Write(Fixture.Intermediate, "public class Intermediate { private ChangedSource first; private OtherSource second; }");
                break;
            case "unreached": paths = Trx(fixture.Root, "UnreachedTests.Fails"); break;
            case "degraded":
                var sources = Enumerable.Range(0, 7).Select(index => $"src/Mcg.AgentOrchestrator.App/Source{index}.cs").ToArray();
                foreach (var source in sources) fixture.Write(source, $"public class {Path.GetFileNameWithoutExtension(source)} {{ }}");
                members[1] = Member('b', sources);
                break;
            case "unreadable": paths = [.. paths, Path.Combine(fixture.Root, "missing.trx")]; break;
            case "apparatus":
                paths = Trx(fixture.Root, ["AlphaTests.Fails"], "DotnetBuildSlotsBusyException: Stable dotnet build slots busy");
                break;
            case "guard":
                fixture.Write(Fixture.TestDirectory + "/WorkflowDecisionCoverageRatchetTests.cs",
                    "public class WorkflowDecisionCoverageRatchetTests { private Intermediate value; " +
                    "[Xunit.Fact] public void EveryUndecidedSite_IsAllowListedByFileAndMember() { } }");
                Assert.Contains("WorkflowDecisionCoverageRatchetTests",
                    RepositoryTestReach.TryReadReachableTestClasses(fixture.Root, [Fixture.Source])!);
                paths = Trx(fixture.Root, "WorkflowDecisionCoverageRatchetTests.EveryUndecidedSite_IsAllowListedByFileAndMember");
                break;
            case "deleted-source":
                members[1] = Member('b', Fixture.Other, "src/Mcg.AgentOrchestrator.App/Deleted.cs");
                break;
            case "split-owners":
                fixture.Write(Fixture.TestDirectory + "/ZebraTests.cs",
                    "public class ZebraTests { private OtherSource value; [Xunit.Fact] public void Fails() { } }");
                break;
        }
        Assert.Null(MergeTrainReachAttribution.TryAttribute(paths, fixture.Root, members, out var classes));
        Assert.Empty(classes);
    }

    private static Fixture CreateFixture()
    {
        var fixture = new Fixture();
        fixture.Write(Fixture.Intermediate, "public class Intermediate { private ChangedSource value; }");
        foreach (var name in new[] { "AlphaTests", "ZebraTests" })
            fixture.Write($"{Fixture.TestDirectory}/{name}.cs",
                $"public class {name} {{ private Intermediate value; [Xunit.Fact] public void Fails() {{ }} }}");
        return fixture;
    }

    private static MergeTrainMemberBinding Member(char id, params string[] paths) => new(new GoalId(new string(id, 32)),
        new string(id, 40), new string(id, 40), paths, [$"member:{id}"], ChangeRiskTier.Behavior,
        ConductorTransitionDecision.Auto, GateReadyMergeStatus.Clean.ToString(), GateReadyMergeReason.NoConflictsDetected.ToString());

    internal static string[] Trx(string root, params string[] names) => Trx(root, names, null);
    internal static string[] Trx(string root, string[] names, string? message)
    {
        var path = Path.Combine(root, "red.trx");
        new XDocument(new XElement("TestRun", new XElement("Results", names.Select(name =>
            new XElement("UnitTestResult", new XAttribute("testName", "Ns." + name), new XAttribute("outcome", "Failed"),
                message is null ? null : new XElement("Output", new XElement("ErrorInfo", new XElement("Message", message)))))),
            new XElement("ResultSummary", new XAttribute("outcome", "Failed"), new XElement("Counters",
                new XAttribute("total", names.Length), new XAttribute("executed", names.Length),
                new XAttribute("passed", 0), new XAttribute("failed", names.Length))))).Save(path);
        return [path];
    }
}
