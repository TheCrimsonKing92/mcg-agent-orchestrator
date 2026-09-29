public sealed class RuntimeMaintenanceOwnershipTestsVerifiedRoot
{
    [Xunit.Fact]
    public void VerifiedCopyWinsOverDeletedCompileTimeWorktree()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "maintenance-root-tests", Guid.NewGuid().ToString("N"));
        var copy = Path.Combine(sandbox, "verified-copy");
        var staleSource = Path.Combine(sandbox, "deleted-worktree", "tests", "RuntimeMaintenanceOwnershipTests.cs");
        try
        {
            Directory.CreateDirectory(Path.Combine(copy, "tests"));
            Directory.CreateDirectory(Path.Combine(copy, "src", "Mcg.AgentOrchestrator.App", "Dashboard"));
            File.WriteAllText(Path.Combine(copy, ".git"), "gitdir: elsewhere");
            File.WriteAllText(Path.Combine(copy, "Mcg.AgentOrchestrator.sln"), "");

            Xunit.Assert.Equal(copy, CliVerifiedRepositoryRoot.Resolve(
                staleSource,
                name => name == CliVerifiedRepositoryRoot.VariableName ? copy : null));
        }
        finally
        {
            if (Directory.Exists(sandbox))
                Directory.Delete(sandbox, recursive: true);
        }
    }
}
