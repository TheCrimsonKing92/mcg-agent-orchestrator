using System.Reflection;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

// Parallel-safe: each case owns its tree and scopes switches with AsyncLocal; no processes run.
public sealed class ManifestScopedGateImpactPlanTests
{
    private const string CoreSourcePath = "src/Mcg.AgentOrchestrator.Core/Application/Widget.cs";
    private const string ForeignProject = "tests/Foreign.Specs/Foreign.Specs.csproj";

    [Fact]
    public void DeclaredProjects_FilterKeepsDeclaredChecksAndPlanMetadata()
    {
        var declared = ManifestDeclaredTestProjects.From([null, " ", ForeignProject.ToUpperInvariant().Replace('/', '\\')]);
        var native = new RepositoryTestImpactCheck("native", ["dotnet", "test", "--project", ForeignProject], "native reason");
        var legacy = new RepositoryTestImpactCheck("legacy", ["dotnet", "test", ForeignProject, "--filter", "Spec"], "legacy reason");
        var plan = new RepositoryTestImpactPlan(true, true, "original summary",
        [
            native,
            new("undeclared", ["dotnet", "test", "--project", "tests/Other/Other.csproj"], "other"),
            new("no project", ["dotnet", "test"], "default"),
            legacy,
            new("manual", [], "review")
        ], new(ReverseDependencyDegradationKind.Unreadable, "unreadable"), new(1, 10, 2, 20, 3, 30));

        var filtered = declared.Filter(plan);

        Assert.True(declared.Declares(ForeignProject));
        Assert.False(declared.Declares("./" + ForeignProject));
        Assert.False(declared.Declares(null));
        Assert.False(declared.DeclaresAnyBuiltInTestProject);
        Assert.Collection(filtered.Checks, check => Assert.Same(native, check), check => Assert.Same(legacy, check));
        Assert.Equal(plan with { Checks = filtered.Checks }, filtered);
    }

    [Theory]
    [InlineData(AcceptancePolicyShardPlanner.CoreTestsProject)]
    [InlineData(AcceptancePolicyShardPlanner.CliTestsProject)]
    public void ScopedPlan_AnyBuiltInDeclaration_PreservesThePurePlan(string project)
    {
        using var fixture = new Fixture();
        var normalizedDeclaration = project.ToUpperInvariant().Replace('/', '\\');
        Assert.True(ManifestDeclaredTestProjects.From([normalizedDeclaration]).DeclaresAnyBuiltInTestProject);
        AcceptanceManifestCheck[] manifest = [new() { Name = "declared", Type = "DOTNET-TEST", Project = normalizedDeclaration }];
        var summary = RepositoryChangeClassifier.Classify([CoreSourcePath]);
        var expected = RepositoryTestImpactPlanner.Plan(summary, fixture.Root);
        Assert.True(expected.RequiresBuild);
        Assert.NotEmpty(expected.Checks);

        AssertPlanEqual(expected, ManifestScopedImpactPlan.Plan(summary, fixture.Root, manifest));
        AssertPlanEqual(expected, ManifestScopedImpactPlan.Plan(new[] { CoreSourcePath }, fixture.Root, manifest));
    }

    [Theory]
    [InlineData("command", AcceptancePolicyShardPlanner.CoreTestsProject)]
    [InlineData("dotnet-test", null)]
    [InlineData("dotnet-test", ForeignProject)]
    public void ScopedPlan_NoBuiltInTestDeclaration_RemovesUndeclaredChecks(string type, string? project)
    {
        using var fixture = new Fixture();
        AssertUnfilteredMcgPlan(fixture.Root);
        var plan = ManifestScopedImpactPlan.Plan(new[] { CoreSourcePath }, fixture.Root,
            [new() { Type = type, Project = project }]);

        Assert.True(plan.RequiresBuild);
        Assert.Empty(plan.Checks);
    }

    [Fact]
    public void ScopedPlan_NoBuild_PreservesNonExecutableEvidence()
    {
        using var fixture = new Fixture();
        string[] changes = ["docs/readme.md"];
        var expected = RepositoryTestImpactPlanner.Plan(changes, fixture.Root);
        Assert.False(expected.RequiresBuild);
        Assert.NotEmpty(expected.Checks);

        AssertPlanEqual(expected, ManifestScopedImpactPlan.Plan(changes, fixture.Root, []));
    }

    [Fact]
    public void RequiredPolicy_ForeignManifest_InjectsNoMcgChecks()
    {
        using var switches = AcceptanceShardPolicySwitches.Use(new(FullShards: false, ChangeScoped: true));
        using var fixture = new Fixture();
        AcceptanceManifestCheck[] manifest = [new() { Name = "foreign check", Type = "command", Command = "foreign-tool" }];
        fixture.WriteManifest(manifest);
        AssertUnfilteredMcgPlan(fixture.Root);

        Assert.Empty(RequiredChecks(fixture.Root, manifest));
        var checks = GoalAcceptanceVerifier.BuildEffectiveAcceptanceChecksForTests(fixture.Root, [CoreSourcePath]);

        var check = Assert.Single(checks);
        Assert.Equal("foreign check", check.Name);
        Assert.Equal("foreign-tool", check.Command);
        Assert.DoesNotContain(checks, ReferencesMcgProject);
    }

    [Fact]
    public void ChangeScoped_ForeignManifest_ReturnsNullForFullManifestFallback()
    {
        using var switches = AcceptanceShardPolicySwitches.Use(new(FullShards: false, ChangeScoped: true));
        using var fixture = new Fixture();
        AssertUnfilteredMcgPlan(fixture.Root);
        var solution = TestCheck("foreign solution", "Foreign.sln");
        var foreign = TestCheck("foreign specs", ForeignProject);
        AcceptanceManifestCheck[] manifest = [solution, foreign];
        // Effective checks can already contain an injected policy shard. It is not a declaration,
        // and must not rescue an empty filtered plan or prevent the null fallback.
        var injected = TestCheck("injected core", AcceptancePolicyShardPlanner.CoreTestsProject);
        var shardPlan = PolicyShardPlan.Scoped("explicit non-forced plan",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { AcceptancePolicyShardPlanner.CoreTestsProject });
        Assert.False(shardPlan.ForceFull);
        Assert.True(shardPlan.IncludesProject(injected.Project));

        var scoped = ChangeScopedChecks(fixture.Root, solution, [solution, foreign, injected], manifest, shardPlan);

        Assert.Null(scoped);
        fixture.WriteManifest(manifest);
        var fullChecks = GoalAcceptanceVerifier.BuildEffectiveAcceptanceChecksForTests(fixture.Root, [CoreSourcePath]);
        Assert.Equal(manifest.Select(CheckIdentity), fullChecks.Select(CheckIdentity));
        Assert.DoesNotContain(fullChecks, ReferencesMcgProject);
    }

    [Fact]
    public void BothGateSites_BuiltInManifest_KeepsPurePlannerChecks()
    {
        using var switches = AcceptanceShardPolicySwitches.Use(new(FullShards: false, ChangeScoped: true));
        using var fixture = new Fixture();
        var solution = TestCheck("solution", "Mcg.AgentOrchestrator.sln");
        AcceptanceManifestCheck[] manifest = [solution, TestCheck("core tests", AcceptancePolicyShardPlanner.CoreTestsProject)];
        var rawPlan = RepositoryTestImpactPlanner.Plan(new[] { CoreSourcePath }, fixture.Root);
        Assert.True(rawPlan.RequiresBuild);
        Assert.False(rawPlan.RequiresBroadVerification);
        var policy = VerificationPolicyCompiler.Compile(AgentRole.Reviewer, string.Empty, string.Empty, null,
            new[] { CoreSourcePath }, rawPlan);
        var expected = GoalAcceptanceVerifier.MapRequiredPolicyChecksForTests(fixture.Root, policy);
        Assert.NotEmpty(expected);

        Assert.Equal(expected.Select(CheckIdentity), RequiredChecks(fixture.Root, manifest).Select(CheckIdentity));
        // Empty infrastructure lanes omit broad Infrastructure checks, as they did before scoping.
        var expectedScoped = expected.Where(check => !AcceptancePolicyShardPlanner.IsBroadInfrastructureTestCheck(check)).ToArray();
        Assert.NotEmpty(expectedScoped);
        var scoped = ChangeScopedChecks(fixture.Root, solution, manifest, manifest, PolicyShardPlan.NotApplicable("explicit plan"));
        Assert.NotNull(scoped);
        Assert.Equal(expectedScoped.Select(CheckIdentity), scoped.Select(CheckIdentity));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingManifest_ThrowsWithSearchedLocationsAndReturnsNoChecks(bool projectHome)
    {
        using var switches = AcceptanceShardPolicySwitches.Use(new(FullShards: false, ChangeScoped: true));
        using var fixture = new Fixture();
        AssertUnfilteredMcgPlan(fixture.Root);
        var home = projectHome ? Path.Combine(fixture.Root, "project-home") : null;
        IReadOnlyList<AcceptanceManifestCheck>? checks = null;

        var failure = Assert.Throws<MissingAcceptanceManifestException>(() =>
        {
            checks = GoalAcceptanceVerifier.BuildEffectiveAcceptanceChecksForTests(fixture.Root, [CoreSourcePath], home);
        });

        Assert.Null(checks);
        var searched = AcceptanceManifestLocator.SearchedLocations(fixture.Root, home);
        Assert.Equal(searched, failure.SearchedLocations);
        foreach (var location in searched)
            Assert.Contains(location, failure.Message, StringComparison.Ordinal);
    }

    private static void AssertUnfilteredMcgPlan(string root)
    {
        Assert.Same(CandidateTreeProbe.AssumeAllPresent, CandidateTreeProbe.ForRepositoryRoot(root));
        var raw = RepositoryTestImpactPlanner.Plan(new[] { CoreSourcePath }, root);
        Assert.True(raw.RequiresBuild);
        Assert.False(raw.RequiresBroadVerification);
        Assert.Contains(raw.Checks, check => check.Command.Any(argument =>
            argument.Contains("tests/Mcg.AgentOrchestrator.", StringComparison.Ordinal)));
    }

    private static void AssertPlanEqual(RepositoryTestImpactPlan expected, RepositoryTestImpactPlan actual)
    {
        Assert.Equal(expected with { Checks = actual.Checks }, actual);
        Assert.Equal(expected.Checks.Select(check => (check.Name, check.CommandLine, check.Reason,
                check.TestProject, Classes: string.Join("\u001f", check.TestClassSelections ?? []))),
            actual.Checks.Select(check => (check.Name, check.CommandLine, check.Reason,
                check.TestProject, Classes: string.Join("\u001f", check.TestClassSelections ?? []))));
    }

    private static IReadOnlyList<AcceptanceManifestCheck> RequiredChecks(
        string root, IReadOnlyList<AcceptanceManifestCheck> manifest) =>
        Assert.IsAssignableFrom<IReadOnlyList<AcceptanceManifestCheck>>(InvokeGateMethod(
            "BuildRequiredPolicyChecks", [root, new[] { CoreSourcePath }, manifest]));

    private static IReadOnlyList<AcceptanceManifestCheck>? ChangeScopedChecks(
        string root, AcceptanceManifestCheck solution, IReadOnlyList<AcceptanceManifestCheck> effective,
        IReadOnlyList<AcceptanceManifestCheck> manifest, PolicyShardPlan shardPlan) =>
        // Parameter order: root, solution, effective checks, raw manifest checks, changes, plan, lanes.
        (IReadOnlyList<AcceptanceManifestCheck>?)InvokeGateMethod("BuildChangeScopedChecks",
            [root, solution, effective, manifest, new[] { CoreSourcePath }, shardPlan, Array.Empty<AcceptanceTestLane>()]);

    private static object? InvokeGateMethod(string name, object?[] arguments)
    {
        var method = typeof(GoalAcceptanceVerifier).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method.Invoke(null, arguments);
    }

    private static AcceptanceManifestCheck TestCheck(string name, string project) =>
        new() { Name = name, Type = "dotnet-test", Project = project, Arguments = ["--verbosity", "minimal"] };

    private static (string Name, string Type, string? Project, string Arguments) CheckIdentity(AcceptanceManifestCheck check) =>
        (check.Name, check.Type, check.Project, string.Join("\u001f", check.Arguments));

    private static bool ReferencesMcgProject(AcceptanceManifestCheck check) =>
        new[] { check.Project, check.Command }.Concat(check.Arguments).Any(value =>
            value?.Replace('\\', '/').Contains("tests/Mcg.AgentOrchestrator.", StringComparison.OrdinalIgnoreCase) == true);

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = SharedTestSupport.CreateTempDirectory();

        public Fixture()
        {
            File.WriteAllText(Path.Combine(Root, ".git"), "gitdir: isolated-fixture");
            var source = Path.Combine(Root, CoreSourcePath);
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            File.WriteAllText(source, "public class Widget { }");
        }

        public void WriteManifest(IReadOnlyList<AcceptanceManifestCheck> checks)
        {
            var config = Path.Combine(Root, "config");
            Directory.CreateDirectory(config);
            File.WriteAllText(Path.Combine(config, "acceptance-manifest.json"), JsonSerializer.Serialize(
                new { engine = new { enforceStructuralCoverage = false }, checks },
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        }

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }
}
