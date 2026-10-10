using Xunit;

public sealed class ChaosGateSeededRepositoryTemplateTests : ChaosGateTestBase
{
    [Fact]
    public void CreateSeededRepo_WarmTemplate_LaunchesNoGitAndMatchesLaunchedSeed()
    {
        var repositories = new List<string>();
        try
        {
            repositories.Add(CreateSeededRepo());

            string copy;
            using (var launches = InfrastructureTestSupport.CountGitProbeLaunches())
            {
                copy = CreateSeededRepo();
                repositories.Add(copy);
                Assert.Equal(0, launches.Count);
            }

            var reference = CreateSeededRepoByLaunchingGit();
            repositories.Add(reference);
            Assert.Equal(ReadGit(reference, ["rev-parse", "HEAD"]), ReadGit(copy, ["rev-parse", "HEAD"]));
            Assert.Equal("Seed", ReadGit(copy, ["log", "-1", "--format=%s"]));
            Assert.Equal("chaos-tests@example.com", ReadGit(copy, ["log", "-1", "--format=%ae"]));
            Assert.Equal("Chaos Tests", ReadGit(copy, ["log", "-1", "--format=%an"]));
            Assert.Equal(DispatchedAt.AddMinutes(-5), DateTimeOffset.Parse(ReadGit(copy, ["log", "-1", "--format=%aI"])));
            Assert.Equal(DispatchedAt.AddMinutes(-5), DateTimeOffset.Parse(ReadGit(copy, ["log", "-1", "--format=%cI"])));
            Assert.Equal("1", ReadGit(copy, ["rev-list", "--count", "HEAD"]));
            Assert.Equal("seed", File.ReadAllText(Path.Combine(copy, "seed.txt")));
            Assert.Equal("", ReadGit(copy, ["status", "--short"]));
        }
        finally
        {
            foreach (var root in repositories)
                DeleteGitFixture(root);
        }
    }

    [Fact]
    public void CreateSeededRepo_Copies_AreIndependent()
    {
        var repositories = new List<string>();
        try
        {
            var first = CreateSeededRepo();
            repositories.Add(first);
            var second = CreateSeededRepo();
            repositories.Add(second);
            var firstHead = ReadGit(first, ["rev-parse", "HEAD"]);
            var secondHead = ReadGit(second, ["rev-parse", "HEAD"]);

            File.WriteAllText(Path.Combine(first, "independent.txt"), "first copy only");
            RunGit(first, ["add", "-A"], CommittedAt);
            RunGit(first, ["commit", "-m", "Diverge"], CommittedAt);

            Assert.NotEqual(firstHead, ReadGit(first, ["rev-parse", "HEAD"]));
            Assert.Equal(secondHead, ReadGit(second, ["rev-parse", "HEAD"]));
        }
        finally
        {
            foreach (var root in repositories)
                DeleteGitFixture(root);
        }
    }

    private static void DeleteGitFixture(string? path)
    {
        if (path is null || !Directory.Exists(path)) return;

        // Git stores objects as read-only files on Windows, which blocks recursive deletion.
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
        }

        Directory.Delete(path, recursive: true);
    }
}
