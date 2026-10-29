using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each row owns its repository, worktree, kernel and manifest resolver.
public sealed class GoalTargetImpactPlanScopeTests
{
    private const string BuiltInProject = "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj";
    private const string ForeignProject = "./tests/Widget.Tests/Widget.Tests.csproj";
    private const string ChangedSource = "src/Mcg.AgentOrchestrator.Core/Widget.cs";
    private const string ManifestRelativePath = "project-settings/checks.json";
    private const string FallbackSummary = "no change-scoped checks for this target; using the manifest's declared test checks";
    private const string DeclaredManifest = """
        {"checks":[
          {"name":"widget checks","type":"dotnet-test","project":"./tests/Widget.Tests/Widget.Tests.csproj"},
          {"name":"ignored command","type":"command","project":"ignored.csproj"}]}
        """;

    [Theory]
    [InlineData("missing")]
    [InlineData("unreadable")]
    [InlineData("declared")]
    [InlineData("home")]
    public void ThreeCallSites_UseTargetManifestAndPreserveOwnHomePlan(string manifestState)
    {
        using var fixture = new RepositoryFixture(manifestState);
        var changedFiles = GoalAcceptanceEvidenceBundleBuilder.GetChangedFiles(fixture.Worktree, "main");
        Assert.Equal(new[] { ChangedSource }, changedFiles);
        var summary = RepositoryChangeClassifier.Classify(changedFiles);
        var direct = RepositoryTestImpactPlanner.Plan(summary, fixture.Worktree);
        // The copied built-in layout must actually exercise the leaking unscoped check.
        Assert.True(direct.RequiresBuild);
        Assert.Contains(direct.Checks, check => check.Command.Contains(BuiltInProject));
        var resolverCalls = 0;
        var home = OrchestratorHome.Resolve(
            manifestState == "home" ? fixture.Root : Path.Combine(fixture.Root, "orchestrator-install"), _ => null);
        var isHome = home.IsHome(OrchestratorWorkspace.ForDirectory(fixture.Root));
        Assert.Equal(manifestState == "home", isHome);
        var target = new WorkerTargetHome(isHome, worktree =>
        {
            Assert.NotEqual("home", manifestState);
            Assert.Equal(fixture.Worktree, worktree);
            resolverCalls++;
            return Path.Combine(worktree, ManifestRelativePath);
        });

        var preReview = ConductorDriver.BuildPreReviewEvidenceContext(fixture.Goal, fixture.Root, "main", target);
        var bundle = GoalAcceptanceEvidenceBundleBuilder.Build(
            fixture.Kernel, fixture.Goal, fixture.Worktree, null, true, null, "main", fixture.Root, target);
        var recovery = GoalRecoveryPlanner.Build(
            fixture.Kernel, fixture.Goal, fixture.Root, "main", includeCleanupBackoff: false, targetHome: target);

        Assert.Equal(JsonSerializer.Serialize(summary), JsonSerializer.Serialize(bundle.ChangeSummary));
        Assert.Equal(JsonSerializer.Serialize(summary), JsonSerializer.Serialize(recovery.ChangeSummary));
        var candidateSha = Git(fixture.Worktree, "rev-parse", "HEAD").Trim();
        if (manifestState == "home")
        {
            AssertPlansEqual(direct, bundle.TestImpactPlan);
            AssertPlansEqual(direct, recovery.TestImpactPlan);
            var expected = PreReviewEvidenceContextBuilder.Build(candidateSha, summary, direct);
            Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(preReview));
            Assert.Equal(0, resolverCalls);
            return;
        }

        foreach (var plan in new[] { bundle.TestImpactPlan, recovery.TestImpactPlan })
        {
            Assert.True(plan.RequiresBuild);
            AssertMetadataEqual(direct, plan);
            Assert.DoesNotContain("tests/Mcg.AgentOrchestrator.", JsonSerializer.Serialize(plan));
            Assert.Contains(ManifestRelativePath, plan.Summary);
            if (manifestState == "declared")
            {
                var check = Assert.Single(plan.Checks);
                Assert.Equal("widget checks", check.Name);
                Assert.Equal(new[] { "dotnet", "test", "--project", ForeignProject }, check.Command);
                Assert.Contains(FallbackSummary, plan.Summary);
            }
            else
            {
                Assert.Empty(plan.Checks);
                Assert.Equal($"acceptance manifest is {manifestState} at {ManifestRelativePath}; no project checks derived", plan.Summary);
            }
        }
        Assert.Equal(3, resolverCalls);
        Assert.Empty(preReview.SelectedFocusedTests);
        Assert.Null(preReview.FocusedRequest);
        Assert.True(preReview.NoApplicableTests);
        Assert.False(preReview.MappingNeedsInput);
        Assert.DoesNotContain("tests/Mcg.AgentOrchestrator.", preReview.MappingReason);
        Assert.Contains(bundle.TestImpactPlan.Summary, preReview.MappingReason);
        var expectedContext = PreReviewEvidenceContextBuilder.Build(candidateSha, summary, bundle.TestImpactPlan);
        Assert.Equal(JsonSerializer.Serialize(expectedContext), JsonSerializer.Serialize(preReview));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Scope_UnavailableManifest_ForcesBuildEvenForNoBuildInput(bool unreadable)
    {
        using var fixture = new ManifestFixture(unreadable ? "{ broken json" : null);
        var plan = SyntheticPlan() with { RequiresBuild = false };
        var actual = GoalTargetImpactPlanScope.Scope(plan, fixture.Worktree, fixture.Manifest);

        Assert.True(actual.RequiresBuild);
        Assert.Empty(actual.Checks);
        Assert.Equal($"acceptance manifest is {(unreadable ? "unreadable" : "missing")} at {ManifestRelativePath}; no project checks derived", actual.Summary);
        AssertMetadataEqual(plan, actual);
    }

    [Fact]
    public void Scope_DeclaredChecksSurviveInInputOrderWithoutAddingFallback()
    {
        using var fixture = new ManifestFixture(DeclaredManifest);
        var first = new RepositoryTestImpactCheck("first", ["dotnet", "test", "--project", ForeignProject], "first reason");
        var second = new RepositoryTestImpactCheck("second", ["dotnet", "test", ForeignProject], "legacy positional check");
        var plan = SyntheticPlan() with { Checks = [ProjectCheck(BuiltInProject), first, second, new("no command", [], "ignored")] };

        var actual = GoalTargetImpactPlanScope.Scope(plan, fixture.Worktree, fixture.Manifest);

        Assert.Equal(new[] { first, second }, actual.Checks);
        Assert.Equal(plan.RequiresBuild, actual.RequiresBuild);
        AssertMetadataEqual(plan, actual);
        Assert.Equal($"{plan.Summary}; checks scoped to dotnet-test projects declared by {ManifestRelativePath}", actual.Summary);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"checks\":[]}")]
    [InlineData("{\"checks\":[{\"type\":\"command\",\"project\":\"ignored.csproj\"}]}")]
    public void Scope_NoDeclaredTestProjects_RemovesChecksWithoutForcingBuild(string manifest)
    {
        using var fixture = new ManifestFixture(manifest);
        var plan = SyntheticPlan() with { RequiresBuild = false };

        var actual = GoalTargetImpactPlanScope.Scope(plan, fixture.Worktree, fixture.Manifest);

        Assert.False(actual.RequiresBuild);
        Assert.Empty(actual.Checks);
        AssertMetadataEqual(plan, actual);
        Assert.Contains("declares no dotnet-test projects; no project checks derived", actual.Summary);
    }

    [Fact]
    public void Scope_FallbackUsesAllDeclaredChecksWithPathsAsWritten()
    {
        using var fixture = new ManifestFixture(DeclaredManifest);
        File.WriteAllText(fixture.Manifest, """
            {"checks":[
              {"name":"widget","type":"dotnet-test","project":"./tests/Widget.Tests/Widget.Tests.csproj"},
              {"type":"DOTNET-TEST","project":"tests/Other.Tests/Other.Tests.csproj"}]}
            """);
        var plan = SyntheticPlan();

        var actual = GoalTargetImpactPlanScope.Scope(plan, fixture.Worktree, fixture.Manifest);

        Assert.Equal(new[] { ForeignProject, "tests/Other.Tests/Other.Tests.csproj" }, actual.Checks.Select(check => check.Command[3]));
        Assert.Equal(new[] { "widget", "tests/Other.Tests/Other.Tests.csproj" }, actual.Checks.Select(check => check.Name));
        Assert.Contains(FallbackSummary, actual.Summary);
        Assert.True(actual.RequiresBuild);
        AssertMetadataEqual(plan, actual);
    }

    private static RepositoryTestImpactCheck ProjectCheck(string project) =>
        new(project, ["dotnet", "test", "--project", project], "synthetic check");

    private static RepositoryTestImpactPlan SyntheticPlan() => new(
        true, true, "synthetic plan", [ProjectCheck(BuiltInProject)],
        new(ReverseDependencyDegradationKind.Unreadable, "synthetic degradation"), new(1, 2, 3, 4, 5, 6));

    private static void AssertMetadataEqual(RepositoryTestImpactPlan expected, RepositoryTestImpactPlan actual)
    {
        Assert.Equal(expected.RequiresBroadVerification, actual.RequiresBroadVerification);
        Assert.Equal(expected.ReverseDependencyDegradation, actual.ReverseDependencyDegradation);
        Assert.Equal(expected.ReverseDependencyHeadroom, actual.ReverseDependencyHeadroom);
    }

    private static void AssertPlansEqual(RepositoryTestImpactPlan expected, RepositoryTestImpactPlan actual) =>
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));

    private static string Git(string directory, params string[] arguments)
    {
        var result = GitCli.Run(directory, arguments);
        Assert.True(result.Succeeded, $"git {string.Join(' ', arguments)}: {result.Error}");
        return result.Output;
    }

    private sealed class ManifestFixture : IDisposable
    {
        internal string Worktree { get; } = SharedTestSupport.CreateTempDirectory();
        internal string Manifest => Path.Combine(Worktree, ManifestRelativePath);

        internal ManifestFixture(string? json)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Manifest)!);
            if (json is not null)
                File.WriteAllText(Manifest, json);
        }

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Worktree);
    }

    private sealed class RepositoryFixture : IDisposable
    {
        internal string Root { get; } = SharedTestSupport.CreateTempDirectory();
        internal AgentOrchestratorKernel Kernel { get; } = new();
        internal Goal Goal { get; }
        internal string Worktree { get; }
        internal string Manifest => Path.Combine(Worktree, ManifestRelativePath);

        internal RepositoryFixture(string manifestState)
        {
            Goal = Kernel.CreateGoal("Verify target impact plans", [new TaskSpec(TaskId.New(), "Inspect target", AgentRole.Researcher)]);
            MainBranchGitRepositoryTemplate.CopyTo(Root, includeSkillCatalog: false);
            Write(Root, BuiltInProject, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            Write(Root, ChangedSource, "public sealed class Widget { }");
            Write(Root, "config/acceptance-manifest.json", """
                {"checks":[{"name":"decoy","type":"dotnet-test","project":"tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj"}]}
                """);
            // The custom directory exists even when the manifest is absent.
            Directory.CreateDirectory(Path.Combine(Root, "project-settings"));
            if (manifestState != "missing")
                Write(Root, ManifestRelativePath, manifestState == "unreadable" ? "{ broken json" : DeclaredManifest);
            Git(Root, "add", ".");
            Git(Root, "commit", "-m", "Target layout and manifest");
            Worktree = GoalWorktrees.Ensure(Root, Goal.Id, trunkBranch: "main");
            Directory.CreateDirectory(Path.Combine(Worktree, "project-settings"));
            Write(Worktree, ChangedSource, "public sealed class Widget { public int Value => 1; }");
            Git(Worktree, "add", ChangedSource);
            Git(Worktree, "commit", "-m", "Change target source");
        }

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);

        private static void Write(string root, string path, string contents)
        {
            var fullPath = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, contents);
        }
    }
}
