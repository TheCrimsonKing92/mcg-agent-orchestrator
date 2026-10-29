using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;
using ScreeningFixture = ExecutorScreeningRunnerTests.ExecutorScreeningFixture;

// Each fixture owns its directory; git-backed consumers use IsolatedProcessSpawning.
public sealed class FocusedEvidenceRemoteEligibilityTests : GoalAcceptanceVerifierTestBase
{
    internal static readonly string[] MachineLocalKeys = ["xunit:DotnetBuildSlots"];
    private static readonly string[] ShippedMachineLocalKeys =
        ["xunit:EnvMutation", "xunit:ProcessSpawningProcessLocal", "xunit:ProcessSpawning", "xunit:DotnetBuildSlots"];

    [Theory]
    [InlineData("FullyQualifiedName~HostOnlyFixtureTests")]
    [InlineData("FullyQualifiedName~FreeFixtureTests|FullyQualifiedName~HostOnlyFixtureTests")]
    [InlineData("FullyQualifiedName~HostOnlyFixtureTests.Runs")]
    [InlineData("FullyQualifiedName=HostOnlyFixtureTests.Runs")]
    [InlineData("FullyQualifiedName~HostOnlyFixtureAbsent")]
    [InlineData("FullyQualifiedName=hostonlyfixturetests")]
    [InlineData("(Category!=X&FullyQualifiedName~HostOnlyFixtureTests)")]
    public void HostSelectionsStayLocal(string filter)
    {
        using var fixture = new Fixture();
        var decision = fixture.Eligibility.Decide(Focused(filter));
        Assert.False(decision.Eligible);
        Assert.Equal("exclusive-key:host:DotnetBuildSlotsHostOnly", decision.Reason);
        Assert.Equal(new[] { "Dotnet build slots local-only" }, decision.ExcludedLanes);
    }

    [Fact]
    public void CollectionOwnershipKeepsAnOtherwiseUnmatchedClassLocal()
    {
        using var fixture = new Fixture("""
            {"engine":{"laneTestSourceRoots":["tests/Mcg.AgentOrchestrator.Infrastructure.Tests"],"infrastructureTestLanes":[
              {"name":"Process spawning host-bound","filter":"FullyQualifiedName~HostBoundNamedFixtureTests",
               "ownedCollections":["ProcessSpawningHostBound"],"exclusiveResourceKeys":["host:ProcessSpawningHostBound"]}
            ]}}
            """);
        File.WriteAllText(Path.Combine(fixture.SourceRoot, "CollectionOwnedFixtureTests.cs"), """
            [Xunit.Collection("ProcessSpawningHostBound")]
            public class CollectionOwnedFixtureTests { [Xunit.Fact] public void Runs() { } }
            """);
        var decision = fixture.Eligibility.Decide(Focused("FullyQualifiedName~CollectionOwnedFixtureTests"));
        Assert.False(decision.Eligible);
        Assert.Equal("exclusive-key:host:ProcessSpawningHostBound", decision.Reason);
        Assert.Equal(new[] { "Process spawning host-bound" }, decision.ExcludedLanes);
    }

    [Theory]
    [InlineData("Category!=X")]
    [InlineData("FullyQualifiedName!~HostOnlyFixtureTests")]
    [InlineData("FullyQualifiedName!=HostOnlyFixtureTests")]
    public void FiltersWithoutPositiveClassTermsFailClosed(string filter)
    {
        using var fixture = new Fixture();
        AssertFailure(fixture.Eligibility.Decide(Focused(filter)), "unresolved-selection");
    }

    [Fact]
    public void MissingFilterFailsClosed()
    {
        using var fixture = new Fixture();
        AssertFailure(fixture.Eligibility.Decide(new Check { Arguments = [] }), "unresolved-selection");
    }

    [Fact]
    public void ManifestAndSourceResolutionErrorsFailClosed()
    {
        using var invalidManifest = new Fixture("{not json");
        AssertFailure(invalidManifest.Eligibility.Decide(Focused("FullyQualifiedName~AlphaTests")), "lane-resolution-failed");
        using var invalidSource = new Fixture();
        File.WriteAllText(Path.Combine(invalidSource.SourceRoot, "UnresolvedTests.cs"), """
            [Xunit.Collection(Missing.Name)] public class UnresolvedTests { [Xunit.Fact] public void Runs() { } }
            """);
        AssertFailure(invalidSource.Eligibility.Decide(Focused("FullyQualifiedName~AlphaTests")), "lane-resolution-failed");
    }

    [Theory]
    [InlineData("FreeFixtureTests")]
    [InlineData("BuildSlotFixtureTests")]
    [InlineData("AlphaTests")]
    public void EligibleDecisionsEqualTheBaseline(string name)
    {
        using var fixture = new Fixture();
        using var baseline = new Fixture(FixtureManifest(includeHost: false));
        var check = Focused("FullyQualifiedName~" + name);
        var actual = fixture.Eligibility.Decide(check);
        AssertEligible(actual);
        AssertSameDecision(baseline.Eligibility.Decide(check), actual);
    }

    [Fact]
    public void UnkeyedAndMissingManifestsNeedNoSourceScan()
    {
        using var fixture = new Fixture("""
            {"engine":{"infrastructureTestLanes":[{"name":"Free","filter":"FullyQualifiedName~FreeFixtureTests"}]}}
            """);
        using var baseline = new Fixture(FixtureManifest(includeHost: false));
        File.WriteAllText(Path.Combine(fixture.SourceRoot, "UnresolvedTests.cs"), """
            [Xunit.Collection(Missing.Name)] public class UnresolvedTests { [Xunit.Fact] public void Runs() { } }
            """);
        var check = Focused("FullyQualifiedName~AlphaTests");
        var actual = fixture.Eligibility.Decide(check);
        AssertEligible(actual);
        AssertSameDecision(baseline.Eligibility.Decide(check), actual);
        File.Delete(Path.Combine(fixture.Root, "config", "acceptance-manifest.json"));
        AssertSameDecision(actual, fixture.Eligibility.Decide(check));
    }

    [Fact]
    public void MultipleAndInlineFiltersUnionTheirPositiveTerms()
    {
        using var fixture = new Fixture();
        var check = new Check
        { Arguments = ["--filter", "FullyQualifiedName~FreeFixtureTests", "--filter=FullyQualifiedName~HostOnlyFixtureTests"] };
        Assert.Equal("exclusive-key:host:DotnetBuildSlotsHostOnly", fixture.Eligibility.Decide(check).Reason);
    }

    [Fact]
    public void CheckLevelReasonPrecedesAllExcludedLanesInManifestOrder()
    {
        using var fixture = new Fixture("""
            {"engine":{"laneTestSourceRoots":["tests/Mcg.AgentOrchestrator.Infrastructure.Tests"],"infrastructureTestLanes":[
              {"name":"First","filter":"FullyQualifiedName~HostOnlyFixture","exclusiveResourceKeys":["host:first"]},
              {"name":"Second","filter":"FullyQualifiedName~HostOnlyFixture","exclusiveResourceKeys":["host:second"]}
            ]}}
            """);
        var check = new Check { Arguments = ["--filter", "FullyQualifiedName~HostOnlyFixtureTests"], ExclusiveResourceKeys = ["host:check"] };
        var decision = fixture.Eligibility.Decide(check);
        Assert.False(decision.Eligible);
        Assert.Equal("exclusive-key:host:check", decision.Reason);
        Assert.Equal(new[] { "First", "Second" }, decision.ExcludedLanes);
        Assert.Equal("exclusive-key:host:first", fixture.Eligibility.Decide(Focused("FullyQualifiedName~HostOnlyFixtureTests")).Reason);
    }

    [Fact]
    public void SharedRulePreservesScreeningReasonsAndKeylessBuildChanges()
    {
        var lanes = AcceptanceGateEngineSettings.Parse("""
            {"engine":{"infrastructureTestLanes":[
              {"name":"Free","filter":"FullyQualifiedName~Free"},
              {"name":"Host","filter":"FullyQualifiedName~Host","exclusiveResourceKeys":["host:Fixture"]},
              {"name":"Build","filter":"FullyQualifiedName~Build","exclusiveResourceKeys":["xunit:EnvMutation"],"requiresBuildSystemChange":true},
              {"name":"Accounting","filter":"FullyQualifiedName~Accounting","exclusiveResourceKeys":["xunit:JobAccounting"]}
            ]}}
            """).InfrastructureTestLanes;
        string[] keys = ["xunit:EnvMutation", "xunit:ProcessSpawning"];
        Assert.Equal(new string?[] { null, "exclusive-key:host:Fixture", "build-system-change", "exclusive-key:xunit:JobAccounting" },
            lanes.Select(lane => RemoteLaneResourceEligibility.ExclusionReason(lane.ExclusiveResourceKeys, lane.RequiresBuildSystemChange, keys)));
        Assert.Null(RemoteLaneResourceEligibility.ExclusionReason([], true, keys));
        Assert.Equal("exclusive-key:XUNIT:EnvMutation", RemoteLaneResourceEligibility.ExclusionReason(["XUNIT:EnvMutation"], false, keys));
    }

    [Fact]
    public void SharedRuleAgreesWithCoordinatorForEveryShippedLane()
    {
        using var fixture = new ScreeningFixture();
        var lanes = AcceptanceGateEngineSettings.Load(InfrastructureTestSupport.FindRepositoryRoot()).InfrastructureTestLanes;
        Assert.NotEmpty(lanes);
        var config = (fixture.Configuration with { MachineLocalResourceKeys = ShippedMachineLocalKeys })
            .ResolveLanes(lanes.Select(lane => "infrastructure tests: " + lane.Name));
        using var coordinator = new RemoteLaneCoordinator(config, new RemoteLaneCandidateIdentity("attempt", "goal", "commit", "tree", "main", "manifest"),
            fixture.Root, null, fixture.Executor, fixture.Clock, TimeSpan.FromMilliseconds(1), null);
        foreach (var lane in lanes)
        {
            var check = new Check
            {
                Name = "infrastructure tests: " + lane.Name, Type = "dotnet-test", Project = ExecutorScreeningPlanner.Project,
                Arguments = ["--filter", lane.Filter], ExclusiveResourceKeys = lane.ExclusiveResourceKeys,
                RequiresBuildSystemChange = lane.RequiresBuildSystemChange
            };
            Assert.Equal(coordinator.IsEligible(check),
                RemoteLaneResourceEligibility.ExclusionReason(lane.ExclusiveResourceKeys, lane.RequiresBuildSystemChange, ShippedMachineLocalKeys) is null);
        }
    }

    [Fact]
    public void EverySinglyOwnedClassAgreesWithItsShippedLaneScreening()
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var classes = AcceptanceTestClassSourceScanner.Scan(root);
        var lanes = AcceptanceLaneMembership.ResolveOwnedCollections(AcceptanceGateEngineSettings.Load(root).InfrastructureTestLanes, classes);
        using var fixture = new ScreeningFixture();
        var config = fixture.Configuration with { MachineLocalResourceKeys = ShippedMachineLocalKeys };
        var eligibility = FocusedEvidenceRemoteEligibility.Create(root, ShippedMachineLocalKeys);
        var exercised = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in classes)
        {
            var owners = AcceptanceLaneMembership.LanesIncluding(lanes, entry.FullName);
            if (owners.Count != 1) continue;
            var lane = owners[0];
            var expected = ExecutorScreeningPlanner.SelectLanes([lane], config).Selected.Contains(lane);
            var actual = eligibility.Decide(Focused("FullyQualifiedName=" + entry.FullName));
            Assert.True(expected == actual.Eligible, $"{entry.FullName} in {lane.Name}: {actual.Reason}");
            exercised.Add(lane.Name);
        }
        Assert.Contains("Dotnet build slots local-only", exercised);
        Assert.Contains("Process spawning host-bound", exercised);
    }

    [Fact]
    public void IncidentFilterIncludesTheHostOnlyBuildLane()
    {
        const string filter = "FullyQualifiedName~AcceptanceDotnetBuildPhaseExecutionCacheTests|FullyQualifiedName~AcceptanceExecutionCacheIsolatedBuildTests|FullyQualifiedName~AssemblyTempRedirectProjectGuardTests|FullyQualifiedName~BuildServerShutdownSourceGuardTests|FullyQualifiedName~CallerFilePathRootSourceGuardTests|FullyQualifiedName~CanaryEngineSurfaceCoverageTests|FullyQualifiedName~CliChildProcessBoundSourceGuardTests|FullyQualifiedName~ConductorLoopHandoffVerificationClockSourceGuardTests|FullyQualifiedName~ConductorRefusedGroupedIdentityGuardTests|FullyQualifiedName~ConsoleReplacementSourceGuardTests|FullyQualifiedName~DashboardRemnantSourceGuardTests|FullyQualifiedName~DirectGitLaunchSourceGuardTests|FullyQualifiedName~DotnetBaseBuildCacheIdentityTests|FullyQualifiedName~DotnetBaseBuildCacheIoTests|FullyQualifiedName~DotnetBaseBuildCacheMissDetailTests|FullyQualifiedName~DotnetBaseBuildCacheValidationTests|FullyQualifiedName~DotnetBuildEnvironmentManagerTests|FullyQualifiedName~ExecutionOwnedGroupCloseSourceGuardTests";
        var eligibility = FocusedEvidenceRemoteEligibility.Create(InfrastructureTestSupport.FindRepositoryRoot(), ShippedMachineLocalKeys);
        var decision = eligibility.Decide(Focused(filter));
        Assert.False(decision.Eligible);
        Assert.Contains("Dotnet build slots local-only", decision.ExcludedLanes);
    }

    internal static Check Focused(string filter) => new()
    {
        Name = "focused: " + filter, Type = "dotnet-test", Runner = "mtp", IsFocusedEvidenceSelection = true,
        Project = ExecutorScreeningPlanner.Project, Arguments = ["--filter", filter]
    };

    internal static string FixtureManifest(bool includeHost = true)
    {
        var lanes = new List<AcceptanceTestLane>
        {
            new("Free", "FullyQualifiedName~FreeFixtureTests"),
            new("Build slots", "FullyQualifiedName~BuildSlotFixtureTests") { ExclusiveResourceKeys = MachineLocalKeys }
        };
        if (includeHost) lanes.Add(new("Dotnet build slots local-only", "FullyQualifiedName~HostOnlyFixture")
        { ExclusiveResourceKeys = ["xunit:DotnetBuildSlots", "host:DotnetBuildSlotsHostOnly"] });
        return JsonSerializer.Serialize(new { version = 1, engine = new
            { laneTestSourceRoots = new[] { "tests/Mcg.AgentOrchestrator.Infrastructure.Tests" }, infrastructureTestLanes = lanes },
            checks = Array.Empty<object>(), forbiddenChangedPathGlobs = Array.Empty<string>() },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    internal static void WriteFixtureSources(string root)
    {
        var directory = Path.Combine(root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
        Directory.CreateDirectory(directory);
        foreach (var name in new[] { "AlphaTests", "FreeFixtureTests", "BuildSlotFixtureTests", "HostOnlyFixtureTests" })
            File.WriteAllText(Path.Combine(directory, name + ".cs"), $"public class {name} {{ [Xunit.Fact] public void Runs() {{ }} }}");
    }

    private static void AssertEligible(FocusedEvidenceRemoteEligibility.Decision decision)
    {
        Assert.True(decision.Eligible, decision.Reason);
        Assert.Null(decision.Reason);
        Assert.Empty(decision.ExcludedLanes);
    }

    private static void AssertFailure(FocusedEvidenceRemoteEligibility.Decision decision, string reason)
    {
        Assert.False(decision.Eligible);
        Assert.Equal(reason, decision.Reason);
        Assert.Empty(decision.ExcludedLanes);
    }

    private static void AssertSameDecision(FocusedEvidenceRemoteEligibility.Decision expected, FocusedEvidenceRemoteEligibility.Decision actual)
    {
        Assert.Equal(expected.Eligible, actual.Eligible);
        Assert.Equal(expected.Reason, actual.Reason);
        Assert.Equal(expected.ExcludedLanes.ToArray(), actual.ExcludedLanes.ToArray());
    }

    internal sealed class Fixture : IDisposable
    {
        internal string Root { get; }
        internal string SourceRoot => Path.Combine(Root, "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests");
        internal FocusedEvidenceRemoteEligibility Eligibility => FocusedEvidenceRemoteEligibility.Create(Root, MachineLocalKeys);
        internal Fixture(string? manifest = null)
        {
            Root = CreateManifestWorkspace(FixtureManifest());
            // Bypass test-default normalization so malformed and no-key manifests remain exact inputs.
            File.WriteAllText(Path.Combine(Root, "config", "acceptance-manifest.json"), manifest ?? FixtureManifest());
            WriteFixtureSources(Root);
        }
        public void Dispose() => DeleteDirectoryWithRetry(Root);
    }
}
