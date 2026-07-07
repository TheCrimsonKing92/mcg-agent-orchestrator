using Mcg.AgentOrchestrator.App.Orchestration;

[Xunit.Collection("EnvMutation")]
public sealed class WorkspaceConsolidatorTests
{
    [Xunit.Fact(DisplayName = "ResolveRepoRoot_finds_git_root_from_nested_subdirectory")]
    public void ResolveRepoRootFindsGitRootFromNestedSubdirectory()
    {
        var root = CreateTempDirectory();
        try
        {
            using var _ = WithConfiguredRepoRoot(null);
            var sub = Path.Combine(root, "src", "deep", "nested");
            Directory.CreateDirectory(sub);
            Directory.CreateDirectory(Path.Combine(root, ".git"));

            var result = OrchestratorWorkspace.ResolveRepoRoot(sub);

            Assert.Equal(Path.GetFullPath(root), result);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "ResolveRepoRoot_falls_back_to_start_directory_when_no_git_root_found")]
    public void ResolveRepoRootFallsBackToStartDirectoryWhenNoGitRootFound()
    {
        var dir = CreatePathOutsideRepository();
        try
        {
            using var _ = WithConfiguredRepoRoot(null);
            var result = OrchestratorWorkspace.ResolveRepoRoot(dir, dir);

            Assert.Equal(Path.GetFullPath(dir), result);
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    [Xunit.Fact(DisplayName = "ResolveRepoRoot_uses_fallback_directory_when_start_directory_has_no_git_root")]
    public void ResolveRepoRootUsesFallbackDirectoryWhenStartDirectoryHasNoGitRoot()
    {
        var start = CreatePathOutsideRepository();
        var fallbackRoot = CreateTempDirectory();
        try
        {
            using var _ = WithConfiguredRepoRoot(null);
            var fallbackSub = Path.Combine(fallbackRoot, "bin", "release");
            Directory.CreateDirectory(fallbackSub);
            Directory.CreateDirectory(Path.Combine(fallbackRoot, ".git"));

            var result = OrchestratorWorkspace.ResolveRepoRoot(start, fallbackSub);

            Assert.Equal(Path.GetFullPath(fallbackRoot), result);
        }
        finally
        {
            DeleteDirectory(start);
            DeleteDirectory(fallbackRoot);
        }
    }

    private static string CreatePathOutsideRepository()
    {
        var basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(basePath))
        {
            basePath = Path.GetPathRoot(Environment.CurrentDirectory)
                ?? throw new InvalidOperationException("Could not determine a root path for an external temp directory.");
        }

        return Path.Combine(basePath, "Temp", "mcg-orchestrator-tests", Guid.NewGuid().ToString("n"));
    }

    [Xunit.Fact(DisplayName = "ResolveRepoRoot_uses_configured_repo_root_before_start_or_fallback")]
    public void ResolveRepoRootUsesConfiguredRepoRootBeforeStartOrFallback()
    {
        var configuredRoot = CreateTempDirectory();
        var startRoot = CreateTempDirectory();
        var fallbackRoot = CreateTempDirectory();
        try
        {
            var startSub = Path.Combine(startRoot, "src", "nested");
            var fallbackSub = Path.Combine(fallbackRoot, "bin", "release");
            Directory.CreateDirectory(startSub);
            Directory.CreateDirectory(fallbackSub);
            Directory.CreateDirectory(Path.Combine(startRoot, ".git"));
            Directory.CreateDirectory(Path.Combine(fallbackRoot, ".git"));
            using var _ = WithConfiguredRepoRoot(configuredRoot);

            var result = OrchestratorWorkspace.ResolveRepoRoot(startSub, fallbackSub);

            Assert.Equal(Path.GetFullPath(configuredRoot), result);
        }
        finally
        {
            DeleteDirectory(configuredRoot);
            DeleteDirectory(startRoot);
            DeleteDirectory(fallbackRoot);
        }
    }

    private static RestoreEnvironmentVariable WithConfiguredRepoRoot(string? value)
    {
        var previous = Environment.GetEnvironmentVariable(OrchestratorWorkspace.RepoRootEnvironmentVariable);
        Environment.SetEnvironmentVariable(OrchestratorWorkspace.RepoRootEnvironmentVariable, value);
        return new RestoreEnvironmentVariable(OrchestratorWorkspace.RepoRootEnvironmentVariable, previous);
    }

    private sealed class RestoreEnvironmentVariable : IDisposable
    {
        private readonly string _name;
        private readonly string? _value;

        public RestoreEnvironmentVariable(string name, string? value)
        {
            _name = name;
            _value = value;
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, _value);
        }
    }

    private static void DeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
