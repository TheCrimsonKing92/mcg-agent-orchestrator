using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its temporary directories and changes no process-wide state.
public sealed class WorkerPromptManifestPathTests
{
    private const string HomeManifestPath = "config/acceptance-manifest.json";

    [Theory]
    [InlineData(TaskComplexity.Complex)]
    [InlineData(TaskComplexity.Simple)]
    public void ProjectStateManifest_ReviewerRequirements_NameTheSelectedManifest(TaskComplexity complexity)
    {
        using var fixture = new ManifestFixture(complexity);
        var stateDirectory = Path.Combine(fixture.Root, "state");
        var manifestPath = Path.Combine(stateDirectory, "acceptance-manifest.json");
        ManifestFixture.WriteManifest(manifestPath);
        Assert.False(File.Exists(fixture.HomeManifest));
        Assert.False(File.Exists(Path.Combine(fixture.Worktree, ".orchestrator", "acceptance-manifest.json")));
        var targetHome = new WorkerTargetHome(false,
            worktree => AcceptanceManifestLocator.Resolve(worktree, stateDirectory));

        var promptPath = WorkerAcceptanceManifestDisplayPath.ForPrompt(targetHome, fixture.Worktree);
        var requirements = RenderRequirements(fixture.BuildBrief(promptPath));

        Assert.Equal(Path.GetFullPath(manifestPath), promptPath);
        Assert.Contains($"Use a test project from `{manifestPath}` by label, file name, or path; never infer a request from prose.",
            requirements, StringComparison.Ordinal);
        Assert.DoesNotContain(HomeManifestPath, requirements, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TaskComplexity.Complex)]
    [InlineData(TaskComplexity.Simple)]
    public void HomeManifest_ReviewerRequirements_PreserveOriginalText(TaskComplexity complexity)
    {
        using var fixture = new ManifestFixture(complexity);
        ManifestFixture.WriteManifest(fixture.HomeManifest);
        var targetHome = new WorkerTargetHome(true, worktree => AcceptanceManifestLocator.Resolve(worktree));
        var promptPath = WorkerAcceptanceManifestDisplayPath.ForPrompt(targetHome, fixture.Worktree);

        var requirements = RenderRequirements(fixture.BuildBrief(promptPath));
        var original = string.Join(Environment.NewLine, OriginalRequirements(complexity));

        Assert.Equal(HomeManifestPath, promptPath);
        Assert.Equal(original, requirements);
        Assert.Contains("Use a test project from `config/acceptance-manifest.json` by label, file name, or path",
            requirements, StringComparison.Ordinal);
        Assert.Equal(original, RenderRequirements(fixture.BuildBrief()));
        Assert.Null(WorkerAcceptanceManifestDisplayPath.ForPrompt(WorkerTargetHome.Home, fixture.Worktree));
        Assert.Null(WorkerAcceptanceManifestDisplayPath.ForPrompt(null, fixture.Worktree));
    }

    [Theory]
    [InlineData(TaskComplexity.Complex)]
    [InlineData(TaskComplexity.Simple)]
    public void MissingManifest_ReviewerRequirements_NameTheLocatorCandidate(TaskComplexity complexity)
    {
        using var fixture = new ManifestFixture(complexity);
        var targetHome = new WorkerTargetHome(false,
            worktree => AcceptanceManifestLocator.Resolve(worktree, Path.Combine(fixture.Root, "state")));
        var selectedPath = targetHome.AcceptanceManifestPath!(fixture.Worktree);
        Assert.False(File.Exists(selectedPath));

        var promptPath = WorkerAcceptanceManifestDisplayPath.ForPrompt(targetHome, fixture.Worktree);
        var requirements = RenderRequirements(fixture.BuildBrief(promptPath));

        Assert.Equal(".orchestrator/acceptance-manifest.json", promptPath);
        Assert.Contains($"Use a test project from `{promptPath}` by label, file name, or path", requirements,
            StringComparison.Ordinal);
        Assert.DoesNotContain(HomeManifestPath, requirements, StringComparison.Ordinal);
        Assert.Equal("missing", WorkerAcceptanceManifestLine.Render(fixture.Worktree, selectedPath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Resolver_NoCandidate_PreservesHomeRequirements(string? resolvedPath)
    {
        using var fixture = new ManifestFixture(TaskComplexity.Simple);
        ManifestFixture.WriteManifest(fixture.HomeManifest);
        var targetHome = new WorkerTargetHome(false, _ => resolvedPath!);

        var promptPath = WorkerAcceptanceManifestDisplayPath.ForPrompt(targetHome, fixture.Worktree);

        Assert.Null(promptPath);
        Assert.Equal(string.Join(Environment.NewLine, OriginalRequirements(TaskComplexity.Simple)),
            RenderRequirements(fixture.BuildBrief(promptPath)));
        Assert.Equal(RenderRequirements(fixture.BuildBrief()), RenderRequirements(fixture.BuildBrief(resolvedPath)));
    }

    [Fact]
    public void Resolver_Fault_Propagates()
    {
        using var fixture = new ManifestFixture(TaskComplexity.Simple);
        var exception = new InvalidOperationException("locator failed");

        Assert.Same(exception, Assert.Throws<InvalidOperationException>(() =>
            WorkerAcceptanceManifestDisplayPath.ForPrompt(new WorkerTargetHome(false, _ => throw exception),
                fixture.Worktree)));
    }

    private static string RenderRequirements(TaskBriefSource source)
    {
        var segment = Assert.Single(source.Segments, item => item.Lines.Any(line =>
            line.StartsWith("- Open blocking test-evidence MUST carry", StringComparison.Ordinal)));
        Assert.Equal(string.Empty, segment.Lines[^1]);
        return string.Join(Environment.NewLine, segment.Lines.SkipLast(1));
    }

    private static IReadOnlyList<string> OriginalRequirements(TaskComplexity complexity)
    {
        var type = typeof(AgentOrchestratorKernel).Assembly.GetType(
            "Mcg.AgentOrchestrator.Core.SdlcRolePromptRequirements", throwOnError: true)!;
        var build = type.GetMethod("Build", [typeof(AgentRole), typeof(TaskComplexity)])!;
        return (IReadOnlyList<string>)build.Invoke(null, [AgentRole.Reviewer, complexity])!;
    }

    private sealed class ManifestFixture : IDisposable
    {
        private readonly AgentOrchestratorKernel kernel = new();
        private readonly TaskSpec task;
        private readonly Goal goal;
        public string Root { get; } = SharedTestSupport.CreateTempDirectory();
        public string Worktree => Path.Combine(Root, "worktree");
        public string HomeManifest => Path.Combine(Worktree, "config", "acceptance-manifest.json");

        public ManifestFixture(TaskComplexity complexity)
        {
            Directory.CreateDirectory(Worktree);
            var description = complexity == TaskComplexity.Complex
                ? "Review the authentication migration in the worker persistence state."
                : "Review the label.";
            task = new TaskSpec(TaskId.New(), description, AgentRole.Reviewer);
            goal = kernel.CreateGoal("Review the authentication migration.", [task]);
            Assert.Equal(complexity, TaskComplexityEstimator.Estimate(task.Description, goal.Objective, AgentRole.Reviewer));
        }

        public TaskBriefSource BuildBrief(string? promptPath = null) =>
            kernel.BuildTaskBriefSource(goal.Id, task.Id, acceptanceManifestPromptPath: promptPath);

        public static void WriteManifest(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{\"checks\":[]}");
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
