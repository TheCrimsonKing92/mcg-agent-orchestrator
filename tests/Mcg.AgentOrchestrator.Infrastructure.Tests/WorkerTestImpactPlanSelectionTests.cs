using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its temporary repository and uses no shared process or mutable state.
public sealed class WorkerTestImpactPlanSelectionTests
{
    private const string AlphaProject = "tests/Alpha.Tests/Alpha.Tests.csproj";
    private const string BetaProject = "tests/Beta.Tests/Beta.Tests.csproj";
    private const string OtherProject = "tests/Other.Tests/Other.Tests.csproj";
    private const string BuiltInProject = "tests/Mcg.AgentOrchestrator.Core.Tests/Mcg.AgentOrchestrator.Core.Tests.csproj";

    [Fact]
    public void HomeTarget_UsesExistingPlanner()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            WriteGoFixture(root);
            string[] paths = ["internal/widget/widget.go"];

            var expected = RepositoryTestImpactPlanner.Plan(paths, root);
            var actual = WorkerTestImpactPlanSelection.For(paths, root, WorkerTargetHome.Home);

            AssertPlansEqual(expected, actual);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public void NonHomeTarget_MissingManifest_HasNoProjectChecksAndReportsMissing()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            WriteGoFixture(root);
            string[] paths = ["internal/widget/widget.go"];

            var actual = WorkerTestImpactPlanSelection.For(paths, root, WorkerTargetHome.NotHome);

            Assert.All(actual.Checks, check => Assert.Empty(check.Command));
            Assert.Contains("manifest is missing at config/acceptance-manifest.json", actual.Summary);
            Assert.DoesNotContain("tests/Mcg.AgentOrchestrator.", actual.Summary);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public void ScopeToManifest_TwoDeclarations_KeepsExactlyDeclaredProjectChecks()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var manifestPath = Path.Combine(root, "config", "acceptance-manifest.json");
            WriteManifest(manifestPath, TwoProjectManifest);
            var plan = SyntheticPlan();

            var actual = WorkerTestImpactPlanSelection.ScopeToManifest(plan, root, manifestPath);

            Assert.Equal(new[] { AlphaProject, BetaProject }, actual.Checks.Select(check => check.Command[3]));
            Assert.Equal(plan.Checks.Take(2), actual.Checks);
            AssertPlansEqual(plan with { Checks = actual.Checks, Summary = actual.Summary }, actual);
            Assert.Contains("checks scoped to dotnet-test projects declared by config/acceptance-manifest.json", actual.Summary);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonHomeTarget_TwoDeclarations_ChecklistNamesSelectedManifest(bool useResolver)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            WriteGoFixture(root);
            var fallbackPath = Path.Combine(root, "config", "acceptance-manifest.json");
            var manifestPath = useResolver ? Path.Combine(root, "state", "project-manifest.json") : fallbackPath;
            if (useResolver)
                WriteManifest(fallbackPath, "{\"checks\":[{\"type\":\"dotnet-test\",\"project\":\"wrong.csproj\"}]}");
            WriteManifest(manifestPath, TwoProjectManifest);
            var targetHome = useResolver ? new WorkerTargetHome(false, _ => manifestPath) : WorkerTargetHome.NotHome;
            var paths = new WorkerGitContext().ReadChangedFilesForTestImpact(root);

            var actual = WorkerTestImpactPlanSelection.For(paths, root, targetHome);
            var displayPath = useResolver ? "state/project-manifest.json" : "config/acceptance-manifest.json";

            // The current foreign planner supplies no foreign project commands; do not invent them.
            var rawPlan = ForeignTargetTestImpactPlanner.Plan(paths, root);
            Assert.NotEmpty(rawPlan.Checks);
            Assert.All(rawPlan.Checks, check => Assert.Empty(check.Command));
            Assert.False(rawPlan.RequiresBuild);
            AssertPlansEqual(rawPlan with { Summary = actual.Summary }, actual);
            Assert.Contains($"checks scoped to dotnet-test projects declared by {displayPath}", actual.Summary);

            var task = new TaskSpec(TaskId.New(), "Verify the target manifest.", AgentRole.Tester);
            var goal = new AgentOrchestratorKernel().CreateGoal("Verify target checks", [task]);
            var contextDirectory = WorkerContextArtifacts.Write(goal, task, root, targetHome: targetHome);
            var checklist = File.ReadAllText(Path.Combine(contextDirectory, "deterministic-verification.md"));

            Assert.Contains($"Acceptance manifest: present: {displayPath} checks: alpha, beta", checklist);
            Assert.Contains(actual.Summary, checklist);
            var impactSection = checklist.Split("## Test Impact Plan", StringSplitOptions.None)[1]
                .Split("## Required Verification Policy", StringSplitOptions.None)[0];
            var expectedImpact = string.Join(Environment.NewLine, new[] { actual.Summary }
                .Concat(actual.Checks.SelectMany(check => new[]
                {
                    $"- {check.Name}: `{check.CommandLine}`", $"  Reason: {check.Reason}"
                })));
            Assert.Equal(expectedImpact, impactSection.Trim());
            Assert.DoesNotContain("tests/Mcg.AgentOrchestrator.", impactSection);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{ not json")]
    [InlineData("{\"checks\":[]}")]
    public void ScopeToManifest_NoBuild_PreservesNonExecutableEvidence(string? json)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var manifestPath = Path.Combine(root, "acceptance-manifest.json");
            if (json is not null)
                WriteManifest(manifestPath, json);
            var plan = ForeignTargetTestImpactPlanner.Plan([], root);
            Assert.False(plan.RequiresBuild);
            Assert.NotEmpty(plan.Checks);
            Assert.All(plan.Checks, check => Assert.Empty(check.Command));

            var actual = WorkerTestImpactPlanSelection.ScopeToManifest(plan, root, manifestPath);

            AssertPlansEqual(plan with { Summary = actual.Summary }, actual);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Theory]
    [InlineData(null, "missing")]
    [InlineData("{ not json", "unreadable")]
    public void ScopeToManifest_UnavailableManifest_RemovesAllChecks(string? json, string availability)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var manifestPath = Path.Combine(root, "acceptance-manifest.json");
            if (json is not null)
                WriteManifest(manifestPath, json);

            Assert.Null(WorkerManifestTestProjectReader.Read(manifestPath));
            var actual = WorkerTestImpactPlanSelection.ScopeToManifest(SyntheticPlan(), root, manifestPath);

            Assert.Empty(actual.Checks);
            Assert.Contains($"manifest is {availability}", actual.Summary);
            Assert.DoesNotContain("tests/Mcg.AgentOrchestrator.", actual.Summary);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public void Read_LockedManifest_ReturnsNoDeclarations()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var manifestPath = Path.Combine(root, "acceptance-manifest.json");
            WriteManifest(manifestPath, TwoProjectManifest);
            using var lockedFile = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.None);

            Assert.Null(WorkerManifestTestProjectReader.Read(manifestPath));
            var actual = WorkerTestImpactPlanSelection.ScopeToManifest(SyntheticPlan(), root, manifestPath);

            Assert.Empty(actual.Checks);
            Assert.Contains("manifest is unreadable", actual.Summary);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"checks\":{}}")]
    [InlineData("{\"checks\":[]}")]
    [InlineData("{\"checks\":[null,1,{},\"entry\",{\"type\":7},{\"type\":\"dotnet-test\",\"project\":7},{\"type\":\"dotnet-test\"},{\"type\":\"dotnet-test\",\"project\":\" \"}]}")]
    [InlineData("{\"engine\":{\"project\":\"tests/Alpha.Tests/Alpha.Tests.csproj\"},\"checks\":[{\"type\":\"command\",\"project\":\"tests/Alpha.Tests/Alpha.Tests.csproj\"}]}")]
    public void ScopeToManifest_NoUsableDeclarations_RemovesChecksWithoutMissing(string json)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var manifestPath = Path.Combine(root, "acceptance-manifest.json");
            WriteManifest(manifestPath, json);

            Assert.NotNull(WorkerManifestTestProjectReader.Read(manifestPath));
            var actual = WorkerTestImpactPlanSelection.ScopeToManifest(SyntheticPlan(), root, manifestPath);

            Assert.Empty(actual.Checks);
            Assert.Contains("checks scoped to dotnet-test projects", actual.Summary);
            Assert.DoesNotContain("missing", actual.Summary);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public void Read_MixedEntries_PreservesProjectPathsAndUsesExistingNormalization()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var manifestPath = Path.Combine(root, "acceptance-manifest.json");
            WriteManifest(manifestPath, """
                {"checks":[null,{},
                  {"type":"DOTNET-TEST","project":"tests\\Alpha.Tests\\Alpha.Tests.csproj"},
                  {"type":"dotnet-test","project":" tests/Beta.Tests/Beta.Tests.csproj "},
                  {"type":"dotnet-test","project":"./tests/Other.Tests/Other.Tests.csproj"},
                  {"type":"command","project":"ignored.csproj"}]}
                """);

            var declared = Assert.IsType<ManifestDeclaredTestProjects>(WorkerManifestTestProjectReader.Read(manifestPath));

            Assert.True(declared.Declares(AlphaProject.ToUpperInvariant()));
            Assert.True(declared.Declares($" {BetaProject} "));
            Assert.True(declared.Declares($"./{OtherProject}"));
            Assert.False(declared.Declares(BetaProject));
            Assert.False(declared.Declares(OtherProject));
            Assert.False(declared.Declares("ignored.csproj"));
            Assert.False(declared.Declares(Path.Combine(root, AlphaProject)));
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public void HomeTarget_ManifestResolverIsNeverInvoked()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            WriteGoFixture(root);
            string[] paths = ["internal/widget/widget.go"];
            var targetHome = new WorkerTargetHome(true, _ => throw new InvalidOperationException("must not resolve"));

            AssertPlansEqual(RepositoryTestImpactPlanner.Plan(paths, root),
                WorkerTestImpactPlanSelection.For(paths, root, targetHome));
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    private const string TwoProjectManifest = """
        {"checks":[
          {"name":"alpha","type":"dotnet-test","project":"tests/Alpha.Tests/Alpha.Tests.csproj"},
          {"name":"beta","type":"dotnet-test","project":"tests/Beta.Tests/Beta.Tests.csproj"}]}
        """;

    private static void WriteManifest(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    private static RepositoryTestImpactPlan SyntheticPlan() => new(
        true, true, "synthetic impact plan",
        [
            ProjectCheck(AlphaProject), ProjectCheck(BetaProject), ProjectCheck(OtherProject),
            ProjectCheck(BuiltInProject), new("non-project evidence", [], "no command")
        ],
        new(ReverseDependencyDegradationKind.Unreadable, "synthetic degradation"),
        new(1, 2, 3, 4, 5, 6));

    private static RepositoryTestImpactCheck ProjectCheck(string project) =>
        new(project, ["dotnet", "test", "--project", project], "synthetic project check");

    private static void WriteGoFixture(string root)
    {
        File.WriteAllText(Path.Combine(root, ".git"), "gitdir: isolated-fixture");
        File.WriteAllText(Path.Combine(root, "go.mod"), "module example.com/widget\n");
        var directory = Path.Combine(root, "internal", "widget");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "widget.go"), "package widget\n");
    }

    private static void AssertPlansEqual(RepositoryTestImpactPlan expected, RepositoryTestImpactPlan actual)
    {
        Assert.Equal(expected.Summary, actual.Summary);
        Assert.Equal(expected.RequiresBuild, actual.RequiresBuild);
        Assert.Equal(expected.RequiresBroadVerification, actual.RequiresBroadVerification);
        Assert.Equal(expected.ReverseDependencyDegradation, actual.ReverseDependencyDegradation);
        Assert.Equal(expected.ReverseDependencyHeadroom, actual.ReverseDependencyHeadroom);
        Assert.Equal(expected.Checks.Count, actual.Checks.Count);
        for (var index = 0; index < expected.Checks.Count; index++)
        {
            Assert.Equal(expected.Checks[index].Name, actual.Checks[index].Name);
            Assert.Equal(expected.Checks[index].CommandLine, actual.Checks[index].CommandLine);
            Assert.Equal(expected.Checks[index].Reason, actual.Checks[index].Reason);
            Assert.Equal(expected.Checks[index].TestProject, actual.Checks[index].TestProject);
        }
    }
}
