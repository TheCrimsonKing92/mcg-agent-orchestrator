using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

// Every case owns its project tree and TRX; no ambient repository or shared files.
public sealed class MergeTrainImpactAttributionTests
{
    private const string CoreSource = "src/Mcg.AgentOrchestrator.Core/Application/CandidateTreeProbe.cs";
    private const string OtherSource = "src/Mcg.AgentOrchestrator.Core/Application/OtherService.cs";
    private const string CoreTest = "tests/Mcg.AgentOrchestrator.Core.Tests/RepositoryTestImpactFilterResolutionTests.cs";
    private const string InfraTest = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ProbeTests.cs";

    [Fact]
    public void Replay_OnlyCoreSourceMemberImpactsUnchangedCoreTest()
    {
        using var fixture = new Fixture();
        var members = new[] { Member('a', CoreSource), Member('b', "docs/b.md"), Member('c') };
        var plan = RepositoryTestImpactPlanner.Plan(members[0].LandingPaths, fixture.Root);
        Assert.False(plan.RequiresBroadVerification);
        Assert.Null(plan.ReverseDependencyDegradation);
        Assert.Contains(plan.Checks, check => check.TestProject == RepositoryTestProject.Core && check.TestClassSelections is null);
        var paths = fixture.Trx("RepositoryTestImpactFilterResolutionTests");
        Assert.Null(MergeTrainRedAttribution.TryAttribute(paths, fixture.Root, members, out _));
        Assert.Same(members[0], MergeTrainImpactAttribution.TryAttribute(paths, fixture.Root, members, out var classes));
        Assert.Equal(new[] { "RepositoryTestImpactFilterResolutionTests" }, classes);
    }

    [Fact]
    public void ReverseDependency_UniqueMemberCoversAllFatalClasses()
    {
        using var fixture = new Fixture();
        var members = new[] { Member('a', CoreSource), Member('b', "docs/b.md"), Member('c') };
        var plan = RepositoryTestImpactPlanner.Plan(members[0].LandingPaths, fixture.Root);
        Assert.Contains(plan.Checks, check => check.TestClassSelections?.Contains("ProbeTests") == true);
        Assert.Same(members[0], MergeTrainImpactAttribution.TryAttribute(
            fixture.Trx("ProbeTests", "RepositoryTestImpactFilterResolutionTests", "ProbeTests"), fixture.Root, members, out var classes));
        Assert.Equal(new[] { "ProbeTests", "RepositoryTestImpactFilterResolutionTests" }, classes);
    }

    [Theory]
    [InlineData("no-impact")]
    [InlineData("two-filtered")]
    [InlineData("different-members")]
    [InlineData("two-core-sources")]
    [InlineData("core-filtered-peer")]
    [InlineData("degraded")]
    [InlineData("full-suite")]
    [InlineData("unmapped")]
    [InlineData("abandoned-filter")]
    public void UnusableOrAmbiguousPlans_ReturnNull(string scenario)
    {
        using var fixture = new Fixture();
        var members = new[] { Member('a', CoreSource), Member('b'), Member('c') };
        var names = new[] { "RepositoryTestImpactFilterResolutionTests" };
        switch (scenario)
        {
            case "no-impact": members[0] = Member('a'); break;
            case "two-filtered":
                members[0] = Member('a', InfraTest);
                members[1] = Member('b', InfraTest);
                names = ["ProbeTests"];
                break;
            case "different-members":
                members[0] = Member('a', InfraTest);
                members[1] = Member('b', CoreTest);
                names = ["ProbeTests", "RepositoryTestImpactFilterResolutionTests"];
                break;
            case "two-core-sources": members[1] = Member('b', OtherSource); break;
            case "core-filtered-peer":
                members[1] = Member('b', CoreTest);
                Assert.Contains(RepositoryTestImpactPlanner.Plan(members[1].LandingPaths, fixture.Root).Checks,
                    check => check.TestProject == RepositoryTestProject.Core && check.TestClassSelections is not null);
                break;
            case "degraded":
                var sources = Enumerable.Range(0, 6).Select(index => $"src/Mcg.AgentOrchestrator.Core/Source{index}.cs").ToArray();
                foreach (var path in sources) fixture.Write(path, $"public class Source{Path.GetFileNameWithoutExtension(path)} {{ }}");
                members[0] = Member('a', sources);
                Assert.NotNull(RepositoryTestImpactPlanner.Plan(sources, fixture.Root).ReverseDependencyDegradation);
                break;
            case "full-suite":
                fixture.Write("Directory.Build.props", "<Project />");
                members[1] = Member('b', "Directory.Build.props");
                Assert.True(RepositoryTestImpactPlanner.Plan(members[1].LandingPaths, fixture.Root).RequiresBroadVerification);
                break;
            case "unmapped": names = ["AbsentTests"]; break;
            case "abandoned-filter":
                const string helper = "tests/Mcg.AgentOrchestrator.Core.Tests/Helper.cs";
                fixture.Write(helper, "public class Helper { }");
                members[0] = Member('a', helper);
                Assert.Contains(RepositoryTestImpactPlanner.Plan(members[0].LandingPaths, fixture.Root).Checks,
                    check => check.Reason.StartsWith("Focused test selection was abandoned", StringComparison.Ordinal));
                break;
        }
        Assert.Null(MergeTrainImpactAttribution.TryAttribute(fixture.Trx(names), fixture.Root, members, out var classes));
        Assert.Empty(classes);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("locked")]
    [InlineData("guard")]
    [InlineData("no-fatal")]
    [InlineData("apparatus")]
    [InlineData("unidentifiable")]
    public void IncompleteOrGuardEvidence_ReturnNull(string scenario)
    {
        using var fixture = new Fixture();
        var members = new[] { Member('a', CoreSource), Member('b') };
        var paths = fixture.Trx("ProbeTests");
        FileStream? locked = null;
        try
        {
            if (scenario == "missing") paths = [.. paths, Path.Combine(fixture.Root, "missing.trx")];
            if (scenario == "malformed")
            {
                fixture.Write("bad.trx", "<TestRun");
                paths = [.. paths, Path.Combine(fixture.Root, "bad.trx")];
            }
            if (scenario == "locked") locked = new FileStream(paths[0], FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            if (scenario == "guard") paths = fixture.Trx("ProbeTests", "WorkflowDecisionCoverageRatchetTests");
            if (scenario is "no-fatal" or "apparatus" or "unidentifiable")
            {
                var document = XDocument.Load(paths[0]);
                var result = document.Descendants("UnitTestResult").Single();
                if (scenario == "no-fatal") result.SetAttributeValue("outcome", "NotExecuted");
                if (scenario == "unidentifiable") result.SetAttributeValue("testName", "Unknown");
                if (scenario == "apparatus") result.Add(new XElement("Output", new XElement("ErrorInfo",
                    new XElement("Message", "DotnetBuildSlotsBusyException: Stable dotnet build slots busy"))));
                document.Save(paths[0]);
            }
            Assert.Null(MergeTrainImpactAttribution.TryAttribute(paths, fixture.Root, members, out var classes));
            Assert.Empty(classes);
        }
        finally { locked?.Dispose(); }
    }

    private static MergeTrainMemberBinding Member(char id, params string[] paths) => new(new GoalId(new string(id, 32)),
        new string(id, 40), new string(id, 40), paths, [$"member:{id}"], ChangeRiskTier.Behavior,
        ConductorTransitionDecision.Auto, GateReadyMergeStatus.Clean.ToString(),
        GateReadyMergeReason.NoConflictsDetected.ToString(), new string(id, 40));

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"train-impact-{Guid.NewGuid():N}");
        internal Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Root, ".git"));
            Write("src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            var project = "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>" +
                "<ProjectReference Include=\"../../src/Mcg.AgentOrchestrator.Core/Mcg.AgentOrchestrator.Core.csproj\" />" +
                "</ItemGroup></Project>";
            Write("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", project);
            Write("tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj", project);
            Write(CoreSource, "public class CandidateTreeProbe { }");
            Write(OtherSource, "public class OtherService { }");
            Write(CoreTest, "public class RepositoryTestImpactFilterResolutionTests { [Xunit.Fact] public void Fails() { } }");
            Write(InfraTest, "public class ProbeTests { private CandidateTreeProbe probe = new(); [Xunit.Fact] public void Fails() { } }");
        }
        internal void Write(string path, string text)
        {
            var absolute = Path.Combine(Root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
            File.WriteAllText(absolute, text);
        }
        internal string[] Trx(params string[] classes)
        {
            var path = Path.Combine(Root, "red.trx");
            new XDocument(new XElement("TestRun", new XElement("Results", classes.Select(name =>
                new XElement("UnitTestResult", new XAttribute("testName", name == "WorkflowDecisionCoverageRatchetTests"
                    ? name + ".EveryUndecidedSite_IsAllowListedByFileAndMember" : "Ns." + name + ".Fails"),
                    new XAttribute("outcome", "Failed")))))).Save(path);
            return [path];
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
