using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.GoalAcceptanceVerifier)]
public sealed class GoalAcceptanceVerifierTestsWorktreeUntrackedCleanup : GoalAcceptanceVerifierTestBase
{
    [Xunit.Fact]
    public async Task GateRemovesOnlyNewUntrackedNonIgnoredFiles()
    {
        var root = CreateTempDirectory();
        try
        {
            Git(root, "init", "-b", "main");
            Directory.CreateDirectory(Path.Combine(root, "config"));
            File.WriteAllText(Path.Combine(root, ".gitignore"), "*.ignored\n");
            File.WriteAllText(Path.Combine(root, "tracked.txt"), "before");
            File.WriteAllText(Path.Combine(root, "config", "acceptance-manifest.json"),
                AcceptanceManifestTestDefaults.WithEngine("""
                    {
                      "version": 1,
                      "checks": [
                        { "name": "worktree cleanup probe", "type": "command", "command": "git", "arguments": ["diff", "--check"] }
                      ],
                      "forbiddenChangedPathGlobs": []
                    }
                    """));
            Git(root, "add", ".gitignore", "tracked.txt", "config/acceptance-manifest.json");
            Git(root, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-m", "fixture");
            File.WriteAllText(Path.Combine(root, "pre-existing.txt"), "keep");
            Directory.CreateDirectory(Path.Combine(root, "pre-existing-empty"));

            var emitted = new List<string>();
            TestOverrides.OnGateWorktreeCleanupLineForTests = emitted.Add;
            var invoked = false;
            var verifier = new GoalAcceptanceVerifier((_, _, _) =>
            {
                if (!invoked)
                {
                    invoked = true;
                    var cache = Path.Combine(root, "%SystemDrive%", "ProgramData");
                    Directory.CreateDirectory(cache);
                    File.WriteAllText(Path.Combine(cache, "x.db"), "remove");
                    var existingParent = Path.Combine(root, "pre-existing-empty", "new-child");
                    Directory.CreateDirectory(existingParent);
                    File.WriteAllText(Path.Combine(existingParent, "new.db"), "remove");
                    File.WriteAllText(Path.Combine(root, "during.ignored"), "keep");
                    File.WriteAllText(Path.Combine(root, "tracked.txt"), "after");
                }
                return Task.FromResult(new GoalAcceptanceVerifier.CommandResult(0, ""));
            }, TimeProvider.System, testOverrides: TestOverrides);

            var result = await verifier.RunAsync(root);

            Assert.True(invoked);
            Assert.True(result.Passed, result.OutputTail);
            Assert.True(File.Exists(Path.Combine(root, "pre-existing.txt")));
            Assert.True(File.Exists(Path.Combine(root, "during.ignored")));
            Assert.Equal("after", File.ReadAllText(Path.Combine(root, "tracked.txt")));
            Assert.False(Directory.Exists(Path.Combine(root, "%SystemDrive%")));
            Assert.True(Directory.Exists(Path.Combine(root, "pre-existing-empty")));
            Assert.False(File.Exists(Path.Combine(root, "pre-existing-empty", "new-child", "new.db")));
            Assert.Equal(2, emitted.Count);
            Assert.All(emitted, line =>
            {
                Assert.StartsWith("GATE_WORKTREE_UNTRACKED_REMOVED ", line, StringComparison.Ordinal);
                Assert.Contains("check=\"worktree cleanup probe\"", line, StringComparison.Ordinal);
            });
            Assert.Contains(emitted, line => line.Contains("path=%SystemDrive%/ProgramData/x.db", StringComparison.Ordinal));
            Assert.Contains(emitted, line => line.Contains("path=pre-existing-empty/new-child/new.db", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectoryWithRetry(root);
        }
    }

    private static void Git(string root, params string[] arguments)
    {
        var result = GitCli.Run(root, arguments);
        Assert.True(result.Succeeded, result.Error);
    }
}
