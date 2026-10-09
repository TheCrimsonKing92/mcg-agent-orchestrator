public sealed class DocumentationDashboardRemovalGuardTests
{
    [Xunit.Fact]
    public void StuckWorkerGuidanceNamesCliVerbsThatExist()
    {
        const string guidance = "For a stuck worker, use `cancel-dispatch <task-number>` (add `--goal <goal-prefix>` for another goal) or stop an exact known process id with `repo-process-stop --id <pid>`.";
        var root = VerifiedRepositoryRoot.Find();
        foreach (var relativePath in new[] { "AGENTS.md", "CLAUDE.md" })
            Xunit.Assert.Contains(guidance, File.ReadAllText(Path.Combine(root, relativePath)));

        var catalog = File.ReadAllText(Path.Combine(root,
            "src/Mcg.AgentOrchestrator.App/Cli/CliArgumentParser.CommandCatalog.cs"));
        Xunit.Assert.Contains("\"cancel-dispatch\"", catalog);
        Xunit.Assert.Contains("\"repo-process-stop\"", catalog);
    }
}
