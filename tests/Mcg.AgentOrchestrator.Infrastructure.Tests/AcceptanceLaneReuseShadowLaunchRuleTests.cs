using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel safe: syntax fixtures and pure classification, with no processes or shared state.
public sealed class AcceptanceLaneReuseShadowLaunchRuleTests
{
    private const string Source = "src/Fixture/Widget.cs";
    private const string TestPath = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/PlainTests.cs";
    // Keep synthetic launches out of the source guard while preserving the parsed fixture text.
    private const string GitExecutableLiteral = "\"git\"";
    private static readonly ReverseDependencyTestImpactLookupResult Resolved = new(true, [], null, null);

    [Fact]
    public void MentionOnlyAndUnreachedLaunch_IgnoreMarkersAndResourceKeys()
    {
        const string text = """
            public class PlainTests {
                // ProcessStartInfo is inspection data.
                [Fact] public void Example() { Assert.Equal("pwsh", actual); Assert.Equal(nameof(Unused), helperName); }
                private void Unused() { Process.Start("dotnet", "App.dll"); }
            }
            """;
        var inventory = Inventory("PlainTests", text);
        var checks = new[] { Lane("PlainTests", keys: ["xunit:ProcessSpawning"]) };
        var old = Assert.Single(AcceptanceLaneReuseShadowClassifier.Classify([Source], checks, inventory, Resolved).Lanes);
        Assert.Equal("must-run", old.Decision);
        Assert.Equal("always-affected:process-spawning:xunit:ProcessSpawning", old.Reason);
        var rule = Assert.Single(AcceptanceLaneReuseShadowLaunchRule.Classify([Source], checks, inventory, Resolved,
            AcceptanceLaneReuseShadowLaunchContracts.Build([(TestPath, text)], inventory)));
        AssertDecision(rule, "would-reuse", "unaffected");
        Assert.Empty(rule.Provenance.LaunchTargets);
        Assert.Empty(rule.Provenance.UnresolvedContracts);
    }

    [Fact]
    public void ExternalGit_RecordsThreeTargetsAndRetainsDependencyFallback()
    {
        const string text = $$"""
            public class PlainTests {
                [Fact] public void Example() {
                    GitCli.Run(root, args);
                    InfrastructureTestSupport.RunGitProbe(root, args);
                    Process.Start({{GitExecutableLiteral}}, "status");
                }
            }
            public class Helpers {
                public void Run() { Process.Start("dotnet"); }
                public void RunGitProbe() { Process.Start(executable); }
            }
            """;
        var rule = Classify(text);
        AssertDecision(rule, "would-reuse", "unaffected");
        Assert.Equal("reverse-dependency-index", rule.Provenance.DependencyClosureSource);
        Assert.Equal(new[] { "GitCli.Run", "InfrastructureTestSupport.RunGitProbe", "Process.Start" },
            rule.Provenance.LaunchTargets.Select(site => site.SinkName));
        Assert.Equal(new[] { 3, 4, 5 }, rule.Provenance.LaunchTargets.Select(site => site.Line));
        Assert.All(rule.Provenance.LaunchTargets, site =>
        {
            Assert.Equal("PlainTests", site.MemberClass);
            Assert.Equal(TestPath, site.SourcePath);
            Assert.Equal("git", site.Target);
            Assert.Equal("external-tool", site.Kind);
        });
        AssertDecision(Classify(text, lookup: new(true, ["PlainTests"], null, null)),
            "must-run", "references-changed-source:PlainTests");
    }

    [Fact]
    public void CrossFileHelper_ReportsUnresolvedLaunchAtHelperLocation()
    {
        const string text = "public class PlainTests { [Fact] public void Example() { Helpers.Launch(); } }";
        const string helperPath = "tests/Mcg.AgentOrchestrator.TestSupport/Helpers.cs";
        const string helper = """
            public class Helpers {
                public static void Launch() {
                    Process.Start(executable);
                }
            }
            """;
        var rule = Classify(text, extraFiles: [(helperPath, helper)]);
        AssertDecision(rule, "must-run", "launch-target-unresolved:PlainTests");
        var unresolved = Assert.Single(rule.Provenance.UnresolvedContracts);
        Assert.Equal(helperPath, unresolved.SourcePath);
        Assert.Equal(3, unresolved.Line);
        Assert.Equal("Helpers", unresolved.MemberClass);
    }

    [Fact]
    public void ThreeMemberClasses_RequireUnresolvedBinaryAndRepositoryReadLanes()
    {
        const string path = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/ContractTests.cs";
        const string helperPath = "tests/Mcg.AgentOrchestrator.TestSupport/LaunchHelper.cs";
        const string text = """
            public class UnresolvedTests { [Fact] public void Example() { LaunchHelper.Launch(); } }
            public class BinaryTests { [Fact] public void Example() { Process.Start("dotnet", "Mcg.AgentOrchestrator.App.dll"); } }
            public class ReaderTests { [Fact] public void Example() { FindRepositoryRoot(); } }
            """;
        const string helper = """
            public class LaunchHelper {
                public static void Launch() {
                    Process.Start(executable);
                }
            }
            """;
        var names = new[] { "UnresolvedTests", "BinaryTests", "ReaderTests" };
        var inventory = names.Select(name => new AcceptanceTestClassSource(name, [path], text)).ToArray();
        var rules = AcceptanceLaneReuseShadowLaunchRule.Classify([Source], names.Select(name => Lane(name)).ToArray(),
            inventory, Resolved, AcceptanceLaneReuseShadowLaunchContracts.Build([(path, text), (helperPath, helper)], inventory));
        Assert.Equal(3, rules.Count);
        AssertDecision(rules[0], "must-run", "launch-target-unresolved:UnresolvedTests");
        var unresolved = Assert.Single(rules[0].Provenance.UnresolvedContracts);
        Assert.Equal(helperPath, unresolved.SourcePath);
        Assert.Equal(3, unresolved.Line);
        AssertDecision(rules[1], "must-run", "launch-target-repo-binary:BinaryTests");
        Assert.Equal("dotnet", Assert.Single(rules[1].Provenance.LaunchTargets).Target);
        AssertDecision(rules[2], "must-run", "repository-read-unresolved:ReaderTests");
    }

    [Theory]
    [InlineData("Process.Start(\"dotnet\", \"Mcg.AgentOrchestrator.App.dll\");", "launch-target-repo-binary")]
    [InlineData("FindRepositoryRoot();", "repository-read-unresolved")]
    [InlineData("Process.Start(Path.Combine(AppContext.BaseDirectory, name));", "launch-target-repo-binary")]
    public void ReachedRepositoryDependency_RequiresLane(string body, string reason)
    {
        var rule = Classify($"public class PlainTests {{ [Fact] public void Example() {{ {body} }} }}");
        AssertDecision(rule, "must-run", $"{reason}:PlainTests");
        if (reason == "repository-read-unresolved")
            Assert.Equal("FindRepositoryRoot", Assert.Single(rule.Provenance.UnresolvedContracts).SinkName);
    }

    [Fact]
    public void SeededDispatch_RequiresInputAttestationEvenForGitOnly()
    {
        const string path = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/WorkerDispatchTests.cs";
        const string text = "public class PlainTests { [Fact] public void Example() { Process.Start(\"git\"); } }";
        var inventory = Inventory("PlainTests", text, path);
        var rule = Assert.Single(AcceptanceLaneReuseShadowLaunchRule.Classify([Source], [Lane("PlainTests")], inventory,
            Resolved, AcceptanceLaneReuseShadowLaunchContracts.Build([(path, text)], inventory)));
        AssertDecision(rule, "must-run", "seeded-dispatch-input-attestation-missing:PlainTests");
        Assert.Equal("seeded-dispatch-unattested", rule.Provenance.InputAttestation);
        Assert.Equal("external-tool", Assert.Single(rule.Provenance.LaunchTargets).Kind);
    }

    [Theory]
    [InlineData("scripts/Example.ps1")]
    [InlineData("config/acceptance-manifest.json")]
    [InlineData("global.json")]
    [InlineData("Directory.Build.props")]
    [InlineData("src/Fixture/Fixture.csproj")]
    [InlineData("tests/Mcg.AgentOrchestrator.TestSupport/SharedTestSupport.cs")]
    [InlineData("tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Fixtures/ConsoleIoProbe/Program.cs")]
    public void SharedInputChange_RequiresEveryLaneBeforeOtherReasons(string path)
    {
        const string text = """
            public class AlphaTests { [Fact] public void Example() { } }
            public class BetaTests { [Fact] public void Example() { } }
            """;
        AcceptanceTestClassSource[] inventory = [new("AlphaTests", [TestPath], text), new("BetaTests", [TestPath], text)];
        var result = AcceptanceLaneReuseShadowLaunchRule.Classify([path], [Lane("AlphaTests"), Lane("BetaTests")], inventory,
            new(false, [], "Unavailable", "fixture"), AcceptanceLaneReuseShadowLaunchContracts.Build([(TestPath, text)], inventory),
            "fixture-unavailable", "fixture-inventory");
        Assert.Equal(2, result.Count);
        Assert.All(result, rule => AssertDecision(rule, "must-run", $"requires-all-lanes:{path}"));
    }

    [Fact]
    public void GlobalsMembershipAndChangedClass_PreserveOrderedFailClosedReasons()
    {
        const string text = "public class PlainTests { [Fact] public void Example() { Process.Start(executable); } }";
        AssertDecision(Classify(text, paths: [TestPath]), "must-run", "changed-test-class:PlainTests");
        AssertDecision(Classify(text, lookup: new(false, [], "Unavailable", "fixture")),
            "must-run", "dependency-index-degraded:Unavailable");
        AssertDecision(Classify(text, paths: ["docs/note.md"]), "must-run", "change-outside-src-tests:docs/note.md");
        AssertDecision(Classify(text, paths: ["src/Fixture/data.json"]), "must-run", "non-csharp-change:src/Fixture/data.json");
        AssertDecision(Classify(text, paths: []), "must-run", "shadow-unavailable:no-changed-files");
        AssertDecision(Assert.Single(AcceptanceLaneReuseShadowLaunchRule.Classify([Source],
            [Lane("PlainTests", "Unsupported=Value")], [], Resolved, [])), "must-run", "lane-membership-unresolved");
        AssertDecision(Assert.Single(AcceptanceLaneReuseShadowLaunchRule.Classify([Source],
            [Lane("PlainTests")], [], Resolved, [], inventoryFailure: "InvalidDataException")),
            "must-run", "shadow-unavailable:class-inventory:InvalidDataException");
    }

    [Theory]
    [InlineData("git", "external-tool")]
    [InlineData("git.exe", "external-tool")]
    [InlineData("cmd", "external-tool")]
    [InlineData("cmd.exe", "external-tool")]
    [InlineData("sh", "external-tool")]
    [InlineData("powershell", "external-tool")]
    [InlineData("powershell.exe", "external-tool")]
    [InlineData("dotnet", "repo-binary")]
    [InlineData("App.dll", "repo-binary")]
    [InlineData("Probe.exe", "repo-binary")]
    [InlineData("other-tool", "unresolved")]
    public void LiteralExecutable_UsesClosedExternalToolSet(string target, string kind)
    {
        var rule = Classify($"public class PlainTests {{ [Fact] public void Example() {{ Process.Start(\"{target}\"); }} }}");
        Assert.Equal(kind, Assert.Single(rule.Provenance.LaunchTargets).Kind);
    }

    [Theory]
    [InlineData("var p = new ProcessStartInfo { FileName = \"git\" }; Process.Start(p);")]
    [InlineData("var p = new ProcessStartInfo(\"git\"); Process.Start(p);")]
    [InlineData("var p = new ProcessStartInfo(); p.FileName = \"git\"; Process.Start(p);")]
    [InlineData("var p = new Process { StartInfo = new ProcessStartInfo(\"git\") }; p.Start();")]
    [InlineData("OwnedProcessGroup.Launch(\"git\");")]
    [InlineData("ProcessTreeGuiSuppression.Start(\"git\");")]
    [InlineData("WorkerProcessJobs.Start(\"git\");")]
    [InlineData("WorkerProcessRunner.Run(\"git\");")]
    [InlineData("System.Diagnostics.Process.Start(\"git\");")]
    public void SupportedSinkShapes_ResolveLiteralGit(string body)
    {
        var rule = Classify($"public class PlainTests {{ [Fact] public void Example() {{ {body} }} }}");
        AssertDecision(rule, "would-reuse", "unaffected");
        Assert.Equal("git", Assert.Single(rule.Provenance.LaunchTargets).Target);
    }

    [Fact]
    public void InheritedPartialSetupAndMethodGroups_ReachOnlyExecutableMembers()
    {
        const string text = """
            namespace Fixture;
            public partial class PlainTests : BaseTests {
                static PlainTests() { StaticSetup(); }
                public PlainTests() { ConstructorSetup(); }
                private object field = FieldSetup();
                private object Property { get; } = PropertySetup();
                public void InitializeAsync() { InitializeSetup(); }
                public void DisposeAsync() { DisposeAsyncSetup(); }
                public void Dispose() { DisposeSetup(); }
                [Theory] public void Example() { Action action = Helpers.Group; var helper = new Helper(); }
                private void Unused() { Process.Start("dotnet"); }
            }
            public abstract class BaseTests {
                [Fact] public void Inherited() { Helpers.Inherited(); }
            }
            public partial class PlainTests { private void UnusedPart() { Process.Start(exe); } }
            """;
        const string helperPath = "tests/Mcg.AgentOrchestrator.TestSupport/Helpers.cs";
        const string helpers = $$"""
            namespace Fixture;
            public class Helper { public Helper() { Process.Start({{GitExecutableLiteral}}); } }
            public class Helpers {
                public void StaticSetup() { Process.Start({{GitExecutableLiteral}}); }
                public void ConstructorSetup() { Process.Start({{GitExecutableLiteral}}); }
                public void FieldSetup() { Process.Start({{GitExecutableLiteral}}); }
                public void PropertySetup() { Process.Start({{GitExecutableLiteral}}); }
                public void InitializeSetup() { Process.Start({{GitExecutableLiteral}}); }
                public void DisposeAsyncSetup() { Process.Start({{GitExecutableLiteral}}); }
                public void DisposeSetup() { Process.Start({{GitExecutableLiteral}}); }
                public void Group() { Action a = () => Process.Start({{GitExecutableLiteral}}); void Local() { Process.Start({{GitExecutableLiteral}}); } }
                public void Inherited() { Process.Start({{GitExecutableLiteral}}); }
            }
            """;
        var rule = Classify(text, className: "Fixture.PlainTests", extraFiles: [(helperPath, helpers)]);
        AssertDecision(rule, "would-reuse", "unaffected");
        Assert.Equal(11, rule.Provenance.LaunchTargets.Count);
        Assert.All(rule.Provenance.LaunchTargets, site => Assert.Equal(helperPath, site.SourcePath));
    }

    [Theory]
    [InlineData("items.Select(Helpers.ChainedLaunch).ToArray();")]
    [InlineData("Task.Run(Helpers.ChainedLaunch).Wait();")]
    [InlineData("new Thread(Helpers.ChainedLaunch).Start();")]
    [InlineData("items.Select(ChainedLaunch).ToArray();")]
    [InlineData("Task.Run(ChainedLaunch).Wait();")]
    [InlineData("new Thread(ChainedLaunch).Start();")]
    public void ChainedMethodGroup_ReachesUnresolvedLaunch(string body)
    {
        var text = $"public class PlainTests {{ [Fact] public void Example() {{ {body} }} }}";
        const string helperPath = "tests/Mcg.AgentOrchestrator.TestSupport/Helpers.cs";
        const string helpers = """
            public class Helpers {
                public static void ChainedLaunch() {
                    Process.Start(executable);
                }
            }
            """;
        var rule = Classify(text, extraFiles: [(helperPath, helpers)]);
        AssertDecision(rule, "must-run", "launch-target-unresolved:PlainTests");
        var site = Assert.Single(rule.Provenance.UnresolvedContracts);
        Assert.Equal(site, Assert.Single(rule.Provenance.LaunchTargets));
        Assert.Equal("Helpers", site.MemberClass);
        Assert.Equal(helperPath, site.SourcePath);
        Assert.Equal(3, site.Line);
        Assert.Equal("Process.Start", site.SinkName);
        Assert.Equal("executable", site.Target);
    }

    [Fact]
    public void ConditionalHelperCall_ReachesUnresolvedLaunch()
    {
        const string text = """
            public class PlainTests {
                [Fact] public void Example() {
                    Helpers helper = null;
                    helper?.ConditionalLaunch();
                }
            }
            """;
        const string helperPath = "tests/Mcg.AgentOrchestrator.TestSupport/Helpers.cs";
        const string helpers = """
            public class Helpers {
                public void ConditionalLaunch() {
                    Process.Start(executable);
                }
            }
            """;
        var rule = Classify(text, extraFiles: [(helperPath, helpers)]);
        AssertDecision(rule, "must-run", "launch-target-unresolved:PlainTests");
        var site = Assert.Single(rule.Provenance.UnresolvedContracts);
        Assert.Equal(site, Assert.Single(rule.Provenance.LaunchTargets));
        Assert.Equal("Helpers", site.MemberClass);
        Assert.Equal(helperPath, site.SourcePath);
        Assert.Equal(3, site.Line);
        Assert.Equal("Process.Start", site.SinkName);
    }

    [Fact]
    public void ConditionalProcessStart_RecordsRepositoryBinarySink()
    {
        const string text = """
            public class PlainTests {
                [Fact] public void Example() {
                    var process = new Process { StartInfo = new ProcessStartInfo("dotnet") };
                    process?.Start();
                }
            }
            """;
        var rule = Classify(text);
        AssertDecision(rule, "must-run", "launch-target-repo-binary:PlainTests");
        var site = Assert.Single(rule.Provenance.LaunchTargets);
        Assert.Equal("dotnet", site.Target);
        Assert.Equal("repo-binary", site.Kind);
        Assert.Equal(TestPath, site.SourcePath);
        Assert.Equal(4, site.Line);
        Assert.Equal(".Start", site.SinkName);
    }

    [Fact]
    public void MissingDeclarationOrMalformedSyntax_FailsLoudly()
    {
        var inventory = Inventory("PlainTests", "");
        Assert.Throws<InvalidDataException>(() => AcceptanceLaneReuseShadowLaunchContracts.Build([], inventory));
        Assert.Throws<InvalidDataException>(() => AcceptanceLaneReuseShadowLaunchContracts.Build([(TestPath, "class {")], inventory));
    }

    private static AcceptanceLaneReuseShadowLaunchDecision Classify(string text, string className = "PlainTests",
        IReadOnlyList<string>? paths = null, ReverseDependencyTestImpactLookupResult? lookup = null,
        IReadOnlyList<(string Path, string Text)>? extraFiles = null)
    {
        var inventory = Inventory(className, text);
        return Assert.Single(AcceptanceLaneReuseShadowLaunchRule.Classify(paths ?? [Source], [Lane(className)], inventory,
            lookup ?? Resolved, AcceptanceLaneReuseShadowLaunchContracts.Build([(TestPath, text), .. extraFiles ?? []], inventory)));
    }
    private static AcceptanceTestClassSource[] Inventory(string name, string text, string path = TestPath) => [new(name, [path], text)];
    private static void AssertDecision(AcceptanceLaneReuseShadowLaunchDecision rule, string decision, string reason)
    {
        Assert.Equal(decision, rule.Decision);
        Assert.Equal(reason, rule.Reason);
    }
    private static AcceptanceManifestCheck Lane(string name, string? filter = null, string[]? keys = null) => new()
    {
        Name = $"infrastructure tests: {name}", Type = "dotnet-test",
        Project = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj",
        Arguments = ["--filter", filter ?? $"FullyQualifiedName={name}"], ExclusiveResourceKeys = keys ?? []
    };
}
