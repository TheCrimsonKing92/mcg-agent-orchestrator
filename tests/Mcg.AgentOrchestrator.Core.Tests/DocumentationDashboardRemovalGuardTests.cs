public sealed class DocumentationDashboardRemovalGuardTests
{
    [Xunit.Fact]
    public void GuardedCurrentDocumentsDoNotMentionTheRemovedDashboard()
    {
        string[] guardedPaths =
        [
            "./README.md",
            "AGENTS.md",
            "CLAUDE.md",
            "docs/cli-reference.md",
            "docs/architecture.md",
            "docs/operator-runbook.md",
            "docs/state-model.md",
            ".agents/skills/dotnet-windows-build-hygiene/SKILL.md",
            ".agents/skills/orchestrator-worker-verification/SKILL.md",
            ".agents/skills/orchestrator-dogfood/SKILL.md"
        ];
        var root = VerifiedRepositoryRoot.Find();
        var violations = new List<string>();
        foreach (var relativePath in guardedPaths)
        {
            var path = Path.Combine(root, relativePath);
            Xunit.Assert.True(File.Exists(path), $"Guarded document is missing: {relativePath}");
            var lines = File.ReadAllLines(path);
            for (var index = 0; index < lines.Length; index++)
            {
                if (lines[index].Contains("dashboard", StringComparison.OrdinalIgnoreCase))
                    violations.Add($"{relativePath}:{index + 1}: {lines[index]}");
            }
        }

        Xunit.Assert.True(violations.Count == 0,
            "Guarded current documents contain the removed dashboard:\n" + string.Join("\n", violations) +
            "\nA change that deliberately reintroduces the word must edit this guard's file list or rule in the same commit.");
    }

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
