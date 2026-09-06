using Mcg.AgentOrchestrator.Infrastructure;

[Collection("IsolatedProcessSpawning")]
public sealed class RepositorySourceInventoryTests
{
    [Fact]
    public void GitInventoryIncludesTrackedAndUntrackedSource()
    {
        var root = CreateTempDirectory();
        Assert.True(GitCli.Run(root, "init").Succeeded);
        Write(root, ".gitignore", ".orchestrator/" + Environment.NewLine);
        Write(root, "AGENTS.md", "guidance");
        Write(root, "CLAUDE.md", "guidance");
        Write(root, ".agents/skills/example/SKILL.md", "skill");
        Write(root, "src/Feature/FeatureService.cs", "internal sealed class FeatureService { }");
        Write(root, "tests/Feature.Tests/FeatureServiceTests.cs", "internal sealed class FeatureServiceTests { }");
        Assert.True(GitCli.Run(root, "add", ".gitignore", "AGENTS.md", "CLAUDE.md", ".agents", "src", "tests").Succeeded);
        Write(root, "src/Feature/NewSeam.cs", "internal sealed class NewSeam { }");
        Write(root, ".mcg-sandbox/grok-home/bundled/skills/pptx/templates/deck.json", "generated");
        Write(root, ".orchestrator/state.db", "runtime");
        File.AppendAllText(Path.Combine(root, ".git", "info", "exclude"), ".mcg-sandbox/" + Environment.NewLine);

        var inventory = RepositorySourceInventory.Build(root);

        Assert.Equal("git", inventory.Origin);
        Assert.True(inventory.Complete);
        Assert.Contains("AGENTS.md", inventory.Files);
        Assert.Contains("CLAUDE.md", inventory.Files);
        Assert.Contains(".agents/skills/example/SKILL.md", inventory.Files);
        Assert.Contains("src/Feature/FeatureService.cs", inventory.Files);
        Assert.Contains("src/Feature/NewSeam.cs", inventory.Files);
        Assert.Contains("tests/Feature.Tests/FeatureServiceTests.cs", inventory.Files);
        Assert.DoesNotContain(inventory.Files, RepositorySourceInventory.IsExcludedRelativePath);
    }

    [Fact]
    public void FallbackPrunesGeneratedTreesAndKeepsGuidance()
    {
        var root = CreateTempDirectory();
        Write(root, "AGENTS.md", "guidance");
        Write(root, "CLAUDE.md", "guidance");
        Write(root, ".agents/skills/example/SKILL.md", "skill");
        Write(root, "src/Feature/FeatureService.cs", "source");
        Write(root, "tests/Feature.Tests/FeatureServiceTests.cs", "test");
        Write(root, ".MCG-SANDBOX/codex-home/plugins/cache/tool.cs", "generated");
        Write(root, ".orchestrator/state.db", "runtime");

        var inventory = RepositorySourceInventory.Build(root);

        Assert.Equal("filesystem-fallback", inventory.Origin);
        Assert.True(inventory.Complete);
        Assert.Equal(5, inventory.Files.Count);
        Assert.Contains("AGENTS.md", inventory.Files);
        Assert.Contains("CLAUDE.md", inventory.Files);
        Assert.Contains(".agents/skills/example/SKILL.md", inventory.Files);
        Assert.DoesNotContain(inventory.Files, RepositorySourceInventory.IsExcludedRelativePath);
    }

    [Fact]
    public void FallbackSkipsLinkOutsideRoot()
    {
        var root = CreateTempDirectory();
        var outside = CreateTempDirectory();
        Write(root, "src/App.cs", "source");
        Write(outside, "External.cs", "external");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "linked-source"), outside);
            File.CreateSymbolicLink(Path.Combine(root, "linked-file.cs"), Path.Combine(outside, "External.cs"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"Directory symbolic links are unavailable: {ex.Message}");
        }

        var inventory = RepositorySourceInventory.Build(root);

        Assert.Equal("filesystem-fallback", inventory.Origin);
        Assert.False(inventory.Complete);
        Assert.Equal(2, inventory.SkippedLinkBoundaryCount);
        Assert.Contains(inventory.IncompleteReasons, reason => reason.Contains("link boundary", StringComparison.Ordinal));
        Assert.Contains("src/App.cs", inventory.Files);
        Assert.DoesNotContain(inventory.Files, path => path.Contains("External.cs", StringComparison.Ordinal));
    }

    private static void Write(string root, string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
