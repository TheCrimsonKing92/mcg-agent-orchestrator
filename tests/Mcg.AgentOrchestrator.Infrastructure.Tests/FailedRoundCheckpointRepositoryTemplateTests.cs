public sealed class FailedRoundCheckpointRepositoryTemplateTests
{
    [Xunit.Fact]
    public void CopyTo_WarmTemplate_LaunchesNoGitAndPreservesRepositoryShape()
    {
        var root = Path.Combine(Path.GetTempPath(), "failed-round-template-" + Guid.NewGuid().ToString("n"));
        try
        {
            var warm = Path.Combine(root, "warm", "repository");
            FailedRoundCheckpointRepositoryTemplate.CopyTo(warm, Path.Combine(root, "warm", "no-hooks"));
            var copy = Path.Combine(root, "copy", "repository");
            var hooksPath = Path.Combine(root, "copy", "no-hooks");

            using (var launches = InfrastructureTestSupport.CountGitProbeLaunches())
            {
                FailedRoundCheckpointRepositoryTemplate.CopyTo(copy, hooksPath);
                Assert.Equal(0, launches.Count);
            }

            Assert.Equal("main", ProbeGit(copy, "branch", "--show-current"));
            Assert.Equal("1", ProbeGit(copy, "rev-list", "--count", "HEAD"));
            Assert.Equal("Seed", ProbeGit(copy, "log", "-1", "--format=%s"));
            var commitDate = DateTimeOffset.Parse(ProbeGit(copy, "log", "-1", "--format=%cI"));
            Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), commitDate);
            Assert.Equal(FailedRoundCheckpointRepositoryTemplate.PinnedCommitDate, commitDate);
            Assert.Equal(TimeSpan.Zero, commitDate.Offset);
            Assert.Equal("test@example.test", ProbeGit(copy, "config", "--local", "--get", "user.email"));
            Assert.Equal("Checkpoint Test", ProbeGit(copy, "config", "--local", "--get", "user.name"));
            Assert.Equal("false", ProbeGit(copy, "config", "--local", "--get", "commit.gpgsign"));
            Assert.Equal("false", ProbeGit(copy, "config", "--local", "--get", "core.autocrlf"));
            Assert.Equal(hooksPath, ProbeGit(copy, "config", "--local", "--get", "core.hooksPath"));
            Assert.Equal("seed", File.ReadAllText(Path.Combine(copy, "seed.txt")));
            Assert.Equal("ignored.txt\n", File.ReadAllText(Path.Combine(copy, ".gitignore")));
            Assert.Empty(ProbeGit(copy, "status", "--short"));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Xunit.Fact]
    public void CopyTo_DifferentHooksPaths_AreIsolatedAndLeaveTemplateUnchanged()
    {
        var root = Path.Combine(Path.GetTempPath(), "failed-round-template-" + Guid.NewGuid().ToString("n"));
        try
        {
            var first = Path.Combine(root, "first", "repository");
            var second = Path.Combine(root, "second", "repository");
            var firstHooks = Path.Combine(root, "first", "no-hooks");
            var secondHooks = Path.Combine(root, "second #;", "no-hooks");
            FailedRoundCheckpointRepositoryTemplate.CopyTo(first, firstHooks);
            FailedRoundCheckpointRepositoryTemplate.CopyTo(second, secondHooks);

            var firstActual = ProbeGit(first, "config", "--local", "--get", "core.hooksPath");
            var secondActual = ProbeGit(second, "config", "--local", "--get", "core.hooksPath");
            Assert.Equal(firstHooks, firstActual);
            Assert.Equal(secondHooks, secondActual);
            Assert.NotEqual(FailedRoundCheckpointRepositoryTemplate.HooksPathSentinel, firstActual);
            Assert.NotEqual(FailedRoundCheckpointRepositoryTemplate.HooksPathSentinel, secondActual);
            Assert.Equal(FailedRoundCheckpointRepositoryTemplate.HooksPathSentinel,
                ProbeGit(FailedRoundCheckpointRepositoryTemplate.TemplateDirectory, "config", "--local", "--get", "core.hooksPath"));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private static string ProbeGit(string workingDirectory, params string[] arguments)
    {
        var result = InfrastructureTestSupport.RunGitProbe(workingDirectory, arguments);
        InfrastructureTestSupport.RequireCompleteGitOutput(result);
        Assert.True(result.Succeeded, result.ToString());
        return result.StandardOutput.Trim();
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }
}
