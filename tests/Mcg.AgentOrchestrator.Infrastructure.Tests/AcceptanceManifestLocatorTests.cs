using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every test owns its directories and changes no process-wide state.
public sealed class AcceptanceManifestLocatorTests
{
    [Fact]
    public void ProjectHomeManifest_OverridesCommittedManifestForBothLoaders()
    {
        using var fixture = new ManifestFixture();
        fixture.WriteWorktreeManifest();
        fixture.WriteProjectManifest();
        var before = File.ReadAllBytes(fixture.WorktreeManifest);

        Assert.Equal(fixture.ProjectManifest,
            AcceptanceManifestLocator.Resolve(fixture.Worktree, fixture.ProjectHome));
        AssertManifestSelection(fixture, fixture.ProjectHome, "ProjectHomeOnlyLane");
        Assert.Equal(before, File.ReadAllBytes(fixture.WorktreeManifest));
        Assert.False(Directory.Exists(Path.Combine(fixture.Worktree, ".orchestrator")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AbsentProjectManifest_FallsBackToCommittedManifest(bool createHome)
    {
        using var fixture = new ManifestFixture();
        fixture.WriteWorktreeManifest();
        if (createHome)
            Directory.CreateDirectory(fixture.ProjectHome);

        Assert.Equal(fixture.WorktreeManifest,
            AcceptanceManifestLocator.Resolve(fixture.Worktree, fixture.ProjectHome));
        AssertManifestSelection(fixture, fixture.ProjectHome, "WorktreeCommittedLane");
        Assert.False(File.Exists(fixture.ProjectManifest));
        Assert.Equal(createHome, Directory.Exists(fixture.ProjectHome));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public void UnspecifiedProjectHome_PreservesCommittedManifest(string? home)
    {
        using var fixture = new ManifestFixture();
        fixture.WriteWorktreeManifest();
        fixture.WriteProjectManifest();

        Assert.Equal(fixture.WorktreeManifest, AcceptanceManifestLocator.Resolve(fixture.Worktree, home));
        AssertManifestSelection(fixture, home, "WorktreeCommittedLane");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("missing-home")]
    public void WithoutCommittedManifest_PreservesOrchestratorFallback(string? home)
    {
        using var fixture = new ManifestFixture();
        var projectHome = home == "missing-home" ? fixture.ProjectHome : home;
        var legacyPath = Path.Combine(fixture.Worktree, ".orchestrator", "acceptance-manifest.json");

        // Preserve the old result even when the fallback file itself does not exist.
        Assert.Equal(legacyPath, AcceptanceManifestLocator.Resolve(fixture.Worktree, projectHome));
        Assert.False(Directory.Exists(Path.GetDirectoryName(legacyPath)));
        ManifestFixture.WriteManifest(legacyPath, "WorktreeCommittedLane");
        AssertManifestSelection(fixture, projectHome, "WorktreeCommittedLane");
        Assert.Equal(legacyPath, AcceptanceManifestLocator.Resolve(fixture.Worktree, projectHome));
    }

    [Fact]
    public void MalformedProjectManifest_DoesNotFallBackToCommittedManifest()
    {
        using var fixture = new ManifestFixture();
        fixture.WriteWorktreeManifest();
        Directory.CreateDirectory(fixture.ProjectHome);
        File.WriteAllText(fixture.ProjectManifest, "{");

        Assert.Equal(fixture.ProjectManifest,
            AcceptanceManifestLocator.Resolve(fixture.Worktree, fixture.ProjectHome));
        Assert.ThrowsAny<JsonException>(() => AcceptanceGateEngineSettings.Load(fixture.Worktree, fixture.ProjectHome));
        Assert.ThrowsAny<JsonException>(() => GoalAcceptanceVerifier.BuildEffectiveAcceptanceChecksForTests(
            fixture.Worktree, projectHomeDirectory: fixture.ProjectHome));
    }

    [Fact]
    public void WorkspaceAccessor_DefaultSuppliesNoneAndProjectSuppliesItsHome()
    {
        using var fixture = new ManifestFixture();
        var project = OrchestratorWorkspace.ForProject("alpha", fixture.Root, fixture.Worktree);
        var tenantProject = OrchestratorWorkspace.ForProject("alpha", fixture.Root, fixture.Worktree, "tenant");
        Assert.Equal(project.OrchestratorDirectory, project.ProjectHomeDirectoryOrNull);
        Assert.Equal(tenantProject.OrchestratorDirectory, tenantProject.ProjectHomeDirectoryOrNull);
        Assert.Null(OrchestratorWorkspace.ForDirectory(fixture.Root).ProjectHomeDirectoryOrNull);
        Assert.Null(OrchestratorWorkspace.ForProject("DEFAULT", fixture.Root).ProjectHomeDirectoryOrNull);
        Assert.Null(OrchestratorWorkspace.ForProject("default", fixture.Root,
            tenantName: "tenant").ProjectHomeDirectoryOrNull);
    }

    [Fact]
    public void StructuralPreflight_ProjectManifestValidationOverridesWorktree()
    {
        using var fixture = new ManifestFixture();
        fixture.WriteWorktreeManifest();
        var workspace = OrchestratorWorkspace.ForProject("alpha", fixture.Root, fixture.Worktree);
        Directory.CreateDirectory(workspace.OrchestratorDirectory);
        File.WriteAllText(Path.Combine(workspace.OrchestratorDirectory, "acceptance-manifest.json"),
            "{\"engine\":{\"maxConcurrentShards\":0}}");

        var project = DeveloperCompletionStructuralPreflight.Evaluate(
            fixture.Worktree, workspace.ProjectHomeDirectoryOrNull);
        Assert.True(project.HasViolation);
        Assert.Contains("maxConcurrentShards", project.Message);
        Assert.False(DeveloperCompletionStructuralPreflight.Evaluate(fixture.Worktree).HasViolation);
    }

    [Fact]
    public void StewardReader_ProjectManifestSelectsProjectLaneSubstring()
    {
        using var fixture = new ManifestFixture();
        fixture.WriteWorktreeManifest();
        var workspace = OrchestratorWorkspace.ForProject("alpha", fixture.Root, fixture.Worktree);
        ManifestFixture.WriteManifest(Path.Combine(workspace.OrchestratorDirectory, "acceptance-manifest.json"),
            "ProjectHomeOnlyLane");
        var project = new ManifestConductorStewardLaneSubstringResolver(workspace.ProjectHomeDirectoryOrNull);

        Assert.Equal("ProjectHomeOnlyLane", project.RequiredSubstring(fixture.Worktree, "FixtureCollection"));
        Assert.Equal("WorktreeCommittedLane", new ManifestConductorStewardLaneSubstringResolver()
            .RequiredSubstring(fixture.Worktree, "FixtureCollection"));
    }

    private static void AssertManifestSelection(ManifestFixture fixture, string? home, string expectedLane)
    {
        var settings = AcceptanceGateEngineSettings.Load(fixture.Worktree, home);
        Assert.Equal(expectedLane, Assert.Single(settings.InfrastructureTestLanes).Name);
        Assert.DoesNotContain(settings.InfrastructureTestLanes, lane => lane.Name ==
            (expectedLane == "ProjectHomeOnlyLane" ? "WorktreeCommittedLane" : "ProjectHomeOnlyLane"));
        var checks = GoalAcceptanceVerifier.BuildEffectiveAcceptanceChecksForTests(
            fixture.Worktree, projectHomeDirectory: home);
        Assert.Equal(expectedLane, Assert.Single(checks).Name);
    }

    private sealed class ManifestFixture : IDisposable
    {
        public string Root { get; } = SharedTestSupport.CreateTempDirectory();
        public string Worktree => Path.Combine(Root, "worktree");
        public string ProjectHome => Path.Combine(Root, "project-home");
        public string WorktreeManifest => Path.Combine(Worktree, "config", "acceptance-manifest.json");
        public string ProjectManifest => Path.Combine(ProjectHome, "acceptance-manifest.json");

        public ManifestFixture() => Directory.CreateDirectory(Worktree);

        public void WriteWorktreeManifest() => WriteManifest(WorktreeManifest, "WorktreeCommittedLane");
        public void WriteProjectManifest() => WriteManifest(ProjectManifest, "ProjectHomeOnlyLane");

        public static void WriteManifest(string path, string lane)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                checks = new[] { new { name = lane, type = "no-op" } },
                engine = new
                {
                    infrastructureTestLanes = new[]
                    {
                        new
                        {
                            name = lane,
                            filter = "FullyQualifiedName~" + lane,
                            ownedCollections = new[] { "FixtureCollection" }
                        }
                    }
                }
            }));
        }

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }
}
