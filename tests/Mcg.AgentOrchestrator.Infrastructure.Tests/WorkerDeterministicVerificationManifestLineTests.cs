using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its temporary directories and changes no process-wide state.
public sealed class WorkerDeterministicVerificationManifestLineTests
{
    [Fact]
    public void ProjectStateManifest_TesterContext_ReportsSelectedPathAndChecks()
    {
        using var fixture = new ManifestFixture();
        var workspace = OrchestratorWorkspace.ForProject("foreign", fixture.Worktree, fixture.Worktree,
            dataRootDirectory: Path.Combine(fixture.Root, "state"));
        var manifestPath = Path.Combine(workspace.OrchestratorDirectory, "acceptance-manifest.json");
        ManifestFixture.WriteManifest(manifestPath, """
            {"checks":[{"name":"alpha check"},{"name":"beta check"}]}
            """);
        Assert.False(File.Exists(fixture.WorktreeManifest));
        Assert.False(File.Exists(Path.Combine(fixture.Worktree, ".orchestrator", "acceptance-manifest.json")));
        Assert.Equal(manifestPath, AcceptanceManifestLocator.Resolve(fixture.Worktree, workspace.ProjectHomeDirectoryOrNull));
        Assert.Equal("Acceptance manifest: missing", fixture.WriteManifestLine());

        var line = fixture.WriteManifestLine(new WorkerTargetHome(false, WorkerAcceptanceManifestResolver.For(workspace)));

        Assert.Equal($"Acceptance manifest: present: {manifestPath} checks: alpha check, beta check", line);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HomeManifest_TesterContext_ReportsRelativePathAndChecks(bool supplyResolver)
    {
        using var fixture = new ManifestFixture();
        ManifestFixture.WriteManifest(fixture.WorktreeManifest, """
            {"checks":[{"name":"home first"},{"name":"home second"}]}
            """);
        var targetHome = supplyResolver
            ? new WorkerTargetHome(true, worktree => AcceptanceManifestLocator.Resolve(worktree))
            : null;

        var line = fixture.WriteManifestLine(targetHome);

        Assert.StartsWith("Acceptance manifest: present: config/acceptance-manifest.json", line);
        Assert.Equal("Acceptance manifest: present: config/acceptance-manifest.json checks: home first, home second", line);
    }

    [Fact]
    public void AbsentManifest_TesterContext_ReportsExactlyMissing()
    {
        using var fixture = new ManifestFixture();
        var stateDirectory = Path.Combine(fixture.Root, "state");
        Directory.CreateDirectory(stateDirectory);
        var targetHome = new WorkerTargetHome(false,
            worktree => AcceptanceManifestLocator.Resolve(worktree, stateDirectory));

        Assert.Equal("Acceptance manifest: missing", fixture.WriteManifestLine(targetHome));
    }

    [Fact]
    public void MalformedManifest_TesterContext_ReportsPresentButUnreadable()
    {
        using var fixture = new ManifestFixture();
        var stateDirectory = Path.Combine(fixture.Root, "state");
        var manifestPath = Path.Combine(stateDirectory, "acceptance-manifest.json");
        ManifestFixture.WriteManifest(manifestPath, "{ not json");
        var targetHome = new WorkerTargetHome(false,
            worktree => AcceptanceManifestLocator.Resolve(worktree, stateDirectory));

        Assert.Equal($"Acceptance manifest: present: {manifestPath} checks: unreadable",
            fixture.WriteManifestLine(targetHome));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"checks\":[]}")]
    [InlineData("{\"checks\":{}}")]
    [InlineData("{\"checks\":[null,{}, {\"name\":7}, {\"name\":\" \"}]}")]
    [InlineData("null")]
    public void ReadableManifest_WithoutUsableChecks_ReportsNone(string json)
    {
        using var fixture = new ManifestFixture();
        ManifestFixture.WriteManifest(fixture.WorktreeManifest, json);

        Assert.Equal("Acceptance manifest: present: config/acceptance-manifest.json checks: none",
            fixture.WriteManifestLine());
    }

    [Fact]
    public void CheckNames_MixedCaseAndStringEntries_PreserveDocumentOrder()
    {
        using var fixture = new ManifestFixture();
        ManifestFixture.WriteManifest(fixture.WorktreeManifest, """
            {"Checks":[{"Name":"first"},"second",{"NAME":"third"}]}
            """);

        Assert.Equal("Acceptance manifest: present: config/acceptance-manifest.json checks: first, second, third",
            fixture.WriteManifestLine());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public void Resolver_NoCandidate_DoesNotUseDefaultManifest(string? resolvedPath)
    {
        using var fixture = new ManifestFixture();
        ManifestFixture.WriteManifest(fixture.WorktreeManifest, "{\"checks\":[\"default\"]}");

        Assert.Equal("Acceptance manifest: missing",
            fixture.WriteManifestLine(new WorkerTargetHome(false, _ => resolvedPath!)));
    }

    [Fact]
    public void Resolver_Fault_PropagatesRatherThanReportingMissing()
    {
        using var fixture = new ManifestFixture();
        var exception = new InvalidOperationException("locator failed");

        Assert.Same(exception, Assert.Throws<InvalidOperationException>(() =>
            fixture.WriteManifestLine(new WorkerTargetHome(false, _ => throw exception))));
    }

    private sealed class ManifestFixture : IDisposable
    {
        public string Root { get; } = SharedTestSupport.CreateTempDirectory();
        public string Worktree => Path.Combine(Root, "worktree");
        public string WorktreeManifest => Path.Combine(Worktree, "config", "acceptance-manifest.json");

        public ManifestFixture() => Directory.CreateDirectory(Worktree);

        public static void WriteManifest(string path, string json)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
        }

        public string WriteManifestLine(WorkerTargetHome? targetHome = null)
        {
            var task = new TaskSpec(TaskId.New(), "Verify the project's acceptance checks.", AgentRole.Tester);
            var goal = new AgentOrchestratorKernel().CreateGoal("Verify a project manifest", [task]);
            var contextDirectory = WorkerContextArtifacts.Write(goal, task, Worktree, targetHome: targetHome);
            return Assert.Single(File.ReadAllLines(Path.Combine(contextDirectory, "deterministic-verification.md")),
                line => line.StartsWith("Acceptance manifest:", StringComparison.Ordinal));
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
