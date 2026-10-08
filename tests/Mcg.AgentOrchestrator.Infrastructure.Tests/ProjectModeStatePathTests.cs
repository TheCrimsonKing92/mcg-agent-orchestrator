using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each fact owns its repositories and home; no global git config or env mutation.
public sealed class ProjectModeStatePathTests
{
    [Fact]
    public void ProjectStartup_RepeatedApply_HidesStateAndStopFile()
    {
        using var fixture = new RepositoryFixture();
        var workspace = fixture.Workspace;
        Assert.True(workspace.IsProjectScoped);
        Assert.False(File.Exists(Path.Combine(fixture.Target, ".gitignore")));
        Assert.True(ConductorTargetGitExclude.Apply(workspace));
        Assert.True(ConductorTargetGitExclude.Apply(workspace));
        CreateTargetState(fixture.Target);

        Assert.Empty(fixture.Git("status", "--porcelain=v1", "--untracked-files=all").Trim());
        AssertEntryOnce(fixture.ExcludePath, ".orchestrator/");
        AssertEntryOnce(fixture.ExcludePath, ConductorBatchLoop.StopFileName);
        var projectState = Path.Combine(fixture.DataRoot, "projects", "alpha");
        Assert.Equal(Path.Combine(projectState, "logs", ConductEventLogWriter.CurrentFileName),
            workspace.ConductEventsLogPath);
        Assert.NotEqual(Path.Combine(fixture.Target, ".orchestrator", "logs",
            ConductEventLogWriter.CurrentFileName), workspace.ConductEventsLogPath);
        Assert.False(Directory.Exists(workspace.OrchestratorDirectory));
    }

    [Fact]
    public void ProjectStartup_WithoutApply_StatusReportsBothArtifacts()
    {
        using var fixture = new RepositoryFixture();
        CreateTargetState(fixture.Target);
        var status = fixture.Git("status", "--porcelain=v1", "--untracked-files=all");
        Assert.Contains("?? .conduct-stop", status);
        Assert.Contains("?? .orchestrator/logs/marker.jsonl", status);
    }

    [Fact]
    public void ProjectStartup_LinkedWorktree_UpdatesCommonExclude()
    {
        using var fixture = new RepositoryFixture();
        fixture.Git("-c", "user.name=Test", "-c", "user.email=test@example.invalid",
            "-c", "commit.gpgsign=false", "commit", "--allow-empty", "-m", "fixture");
        var linked = Path.Combine(fixture.Root, "linked");
        fixture.Git("worktree", "add", "-b", "linked", linked);
        var workspace = OrchestratorWorkspace.ForProject("alpha", fixture.Home, linked, dataRootDirectory: fixture.DataRoot);
        Assert.True(File.Exists(Path.Combine(linked, ".git")));
        Assert.Equal(fixture.ExcludePath, RepositoryFixture.ResolveExclude(linked));

        Assert.True(ConductorTargetGitExclude.Apply(workspace));
        Assert.True(ConductorTargetGitExclude.Apply(workspace));
        CreateTargetState(linked);
        Assert.Empty(RepositoryFixture.RunGit(linked, "status", "--porcelain=v1",
            "--untracked-files=all").Trim());
        AssertEntryOnce(fixture.ExcludePath, ".orchestrator/");
        AssertEntryOnce(fixture.ExcludePath, ConductorBatchLoop.StopFileName);
    }

    [Fact]
    public void ProjectStartup_TrackedIgnore_LeavesBytesAndStatusUnchanged()
    {
        using var fixture = new RepositoryFixture();
        var ignore = Path.Combine(fixture.Target, ".gitignore");
        File.WriteAllText(ignore, "unrelated/\r\n");
        fixture.Git("add", ".gitignore");
        fixture.Git("-c", "user.name=Test", "-c", "user.email=test@example.invalid",
            "-c", "commit.gpgsign=false", "commit", "-m", "fixture ignore");
        var before = File.ReadAllBytes(ignore);
        Assert.True(ConductorTargetGitExclude.Apply(fixture.Workspace));
        Assert.True(ConductorTargetGitExclude.Apply(fixture.Workspace));
        CreateTargetState(fixture.Target);
        Assert.Equal(before, File.ReadAllBytes(ignore));
        Assert.Empty(fixture.Git("status", "--porcelain=v1", "--untracked-files=all").Trim());
    }

    [Fact]
    public void LocalExclude_SimilarEntryAndMissingNewline_AppendsWholeLinesOnce()
    {
        using var fixture = new RepositoryFixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.ExcludePath)!);
        File.WriteAllText(fixture.ExcludePath, "# existing\r\n.orchestrator-worktrees/");
        Assert.True(ConductorTargetGitExclude.Apply(fixture.Workspace));
        Assert.True(ConductorTargetGitExclude.Apply(fixture.Workspace));
        AssertEntryOnce(fixture.ExcludePath, ".orchestrator-worktrees/");
        AssertEntryOnce(fixture.ExcludePath, ".orchestrator/");
        AssertEntryOnce(fixture.ExcludePath, ConductorBatchLoop.StopFileName);
    }

    [Fact]
    public void LocalExclude_DuplicateAndInvalidEntries_AddsOnlyValidMissingLines()
    {
        using var fixture = new RepositoryFixture();
        Assert.True(LocalGitExclude.TryAppendEntries(fixture.Target,
            [".orchestrator/", ".orchestrator/", "", "  ", "bad\nentry", "bad\rentry"]));
        var before = File.ReadAllText(fixture.ExcludePath);
        Assert.True(LocalGitExclude.TryAppendEntries(fixture.Target, [".orchestrator/"]));
        Assert.Equal(before, File.ReadAllText(fixture.ExcludePath));
        AssertEntryOnce(fixture.ExcludePath, ".orchestrator/");
        Assert.DoesNotContain("bad", before);
    }

    [Fact]
    public void ProjectStartup_NonProject_DoesNotTouchLocalExclude()
    {
        using var fixture = new RepositoryFixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.ExcludePath)!);
        File.WriteAllText(fixture.ExcludePath, "# preserved\n");
        var before = File.ReadAllBytes(fixture.ExcludePath);
        var messages = new List<string>();
        Assert.False(ConductorTargetGitExclude.Apply(
            OrchestratorWorkspace.ForDirectory(fixture.Target), messages.Add));
        Assert.Equal(before, File.ReadAllBytes(fixture.ExcludePath));
        Assert.Empty(messages);
    }

    [Fact]
    public void ProjectStartup_NonGitTarget_ReturnsFalseAndWarns()
    {
        using var fixture = new RepositoryFixture();
        var nonGit = Path.Combine(fixture.Root, "non-git");
        Directory.CreateDirectory(nonGit);
        var messages = new List<string>();
        Assert.False(ConductorTargetGitExclude.Apply(
            OrchestratorWorkspace.ForProject("alpha", fixture.Home, nonGit, dataRootDirectory: fixture.DataRoot), messages.Add));
        Assert.Single(messages);
        Assert.Contains("Warning:", messages[0]);
        Assert.Empty(Directory.EnumerateFileSystemEntries(nonGit));
    }

    [Fact]
    public void SandboxPreparation_RepeatedApply_HidesScratchWithOneEntry()
    {
        using var fixture = new RepositoryFixture();
        var parameters = new DispatchProcessHost.DispatchRunParameters(
            "unused", fixture.Target, "unused.out", "unused.err", "unused.exit", null, false);
        DispatchProcessHost.PrepareSharedSandboxState(parameters);
        DispatchProcessHost.PrepareSharedSandboxState(parameters);
        var sandbox = Path.Combine(fixture.Target, ".mcg-sandbox");
        Directory.CreateDirectory(sandbox);
        File.WriteAllText(Path.Combine(sandbox, "marker"), "scratch");
        AssertEntryOnce(fixture.ExcludePath, ".mcg-sandbox/");
        Assert.Empty(fixture.Git("status", "--porcelain=v1", "--untracked-files=all").Trim());
    }

    [Fact]
    public void SandboxPreparation_ExistingMention_PreservesLegacySkip()
    {
        using var fixture = new RepositoryFixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.ExcludePath)!);
        File.WriteAllText(fixture.ExcludePath, "# .mcg-sandbox handled by operator\n");
        var before = File.ReadAllBytes(fixture.ExcludePath);
        var parameters = new DispatchProcessHost.DispatchRunParameters(
            "unused", fixture.Target, "unused.out", "unused.err", "unused.exit", null, false);
        DispatchProcessHost.PrepareSharedSandboxState(parameters);
        Assert.Equal(before, File.ReadAllBytes(fixture.ExcludePath));
    }

    [Fact]
    public void RetentionPlan_ProjectWorkspace_TranscriptUsesProjectStateDirectory()
    {
        using var fixture = new RepositoryFixture();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Retention paths",
            [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
        var transcript = Path.Combine(fixture.Workspace.OrchestratorDirectory, "transcripts",
            $"{goal.Id.Value[..8]}.md");
        Directory.CreateDirectory(Path.GetDirectoryName(transcript)!);
        File.WriteAllText(transcript, "project transcript");
        var plan = GoalArtifactRetentionPlanner.Build(kernel, goal, fixture.Workspace);
        var item = Assert.Single(plan.Items, item => item.Kind == RetentionArtifactKind.Transcript);
        Assert.Equal(transcript, item.Path);
        Assert.True(item.Exists);
    }

    [Theory]
    [InlineData("ConductorDriver.cs")]
    [InlineData("ConductorDriver.AcceptanceCohortExecution.cs")]
    [InlineData("ConductorDriver.GateProgressAttribution.cs")]
    public void GateProgress_SourceSites_UseWorkspaceLogPath(string filename)
    {
        var root = InfrastructureTestSupport.FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(root, "src", "Mcg.AgentOrchestrator.App",
            "Orchestration", filename));
        Assert.Contains("workspace.ConductEventsLogPath", source);
        Assert.DoesNotContain("\".orchestrator\"", source);
    }

    private static void CreateTargetState(string target)
    {
        File.WriteAllText(Path.Combine(target, ConductorBatchLoop.StopFileName), "stop");
        var logs = Path.Combine(target, ".orchestrator", "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "marker.jsonl"), "{}\n");
    }

    private static void AssertEntryOnce(string path, string entry) =>
        Assert.Single(File.ReadAllLines(path).Where(line => line.Trim() == entry));

    private sealed class RepositoryFixture : IDisposable
    {
        public string Root { get; } = SharedTestSupport.CreateTempDirectory();
        public string Target => Path.Combine(Root, "target");
        public string Home => Path.Combine(Root, "home");
        public string DataRoot => Path.Combine(Root, "data");
        public OrchestratorWorkspace Workspace => OrchestratorWorkspace.ForProject("alpha", Home, Target, dataRootDirectory: DataRoot);
        public string ExcludePath => ResolveExclude(Target);

        public RepositoryFixture()
        {
            try
            {
                var available = GitCli.Run(Root, "--version");
                if (!available.Succeeded)
                    Assert.Skip("Real git is unavailable: " + available.Error);
                Directory.CreateDirectory(Target);
                Git("-c", "init.templateDir=", "init");
                var emptyExcludes = Path.Combine(Root, "empty-excludes");
                File.WriteAllText(emptyExcludes, string.Empty);
                Git("config", "core.excludesFile", emptyExcludes);
                Git("config", "core.hooksPath", Path.Combine(Root, "no-hooks"));
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public string Git(params string[] args) => RunGit(Target, args);

        internal static string RunGit(string target, params string[] args)
        {
            var result = GitCli.Run(target, args);
            Assert.True(result.Succeeded, $"git {string.Join(' ', args)}: {result.Error}");
            return result.Output;
        }

        internal static string ResolveExclude(string target)
        {
            var path = RunGit(target, "rev-parse", "--git-path", "info/exclude").Trim();
            return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(target, path));
        }

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }
}
