using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class WorkspaceConsolidatorTests
{
    [Xunit.Fact(DisplayName = "ResolveRepoRoot_finds_solution_root_from_nested_subdirectory")]
    public void ResolveRepoRootFindsSolutionRootFromNestedSubdirectory()
    {
        var root = CreateTempDirectory();
        try
        {
            var sub = Path.Combine(root, "src", "deep", "nested");
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(root, "Mcg.AgentOrchestrator.sln"), string.Empty);

            var result = OrchestratorWorkspace.ResolveRepoRoot(sub);

            Assert.Equal(Path.GetFullPath(root), result);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Xunit.Fact(DisplayName = "ResolveRepoRoot_falls_back_to_start_directory_when_no_solution_found")]
    public void ResolveRepoRootFallsBackToStartDirectoryWhenNoSolutionFound()
    {
        var dir = CreateTempDirectory();
        try
        {
            var result = OrchestratorWorkspace.ResolveRepoRoot(dir, dir);

            Assert.Equal(Path.GetFullPath(dir), result);
        }
        finally
        {
            DeleteDirectory(dir);
        }
    }

    [Xunit.Fact(DisplayName = "ResolveRepoRoot_uses_fallback_directory_when_start_directory_has_no_solution")]
    public void ResolveRepoRootUsesFallbackDirectoryWhenStartDirectoryHasNoSolution()
    {
        var start = CreateTempDirectory();
        var fallbackRoot = CreateTempDirectory();
        try
        {
            var fallbackSub = Path.Combine(fallbackRoot, "bin", "release");
            Directory.CreateDirectory(fallbackSub);
            File.WriteAllText(Path.Combine(fallbackRoot, "Mcg.AgentOrchestrator.sln"), string.Empty);

            var result = OrchestratorWorkspace.ResolveRepoRoot(start, fallbackSub);

            Assert.Equal(Path.GetFullPath(fallbackRoot), result);
        }
        finally
        {
            DeleteDirectory(start);
            DeleteDirectory(fallbackRoot);
        }
    }

    private static void DeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
