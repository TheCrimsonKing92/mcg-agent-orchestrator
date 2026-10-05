public sealed class WorkerSkillCatalogRepositoryGuardTests
{
    [Xunit.Fact]
    public void FileLockGuidanceIdentifiesHolderWithoutBuildServerShutdown()
    {
        var root = VerifiedRepositoryRoot.Find();
        var text = File.ReadAllText(Path.Combine(root, ".agents", "skills",
            "dotnet-windows-build-hygiene", "SKILL.md"));

        Assert.DoesNotContain("1. Run `dotnet build-server shutdown`.", text, StringComparison.Ordinal);
        Assert.Contains("Roslyn and MSBuild build servers are disabled repo-wide", text, StringComparison.Ordinal);
        Assert.Contains("Directory.Build.props", text, StringComparison.Ordinal);
        Assert.Contains("Directory.Build.rsp", text, StringComparison.Ordinal);
        Assert.Contains("do not run a build-server shutdown; identify the exact process holding the file first.",
            text, StringComparison.Ordinal);
        Assert.Contains("Retry the same narrow command once.", text, StringComparison.Ordinal);
        Assert.Contains("Stop only exact known stale processes; never run broad cleanup such as killing every " +
            "`codex`, `dotnet`, or app process.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("dashboard", text, StringComparison.OrdinalIgnoreCase);
    }

}
