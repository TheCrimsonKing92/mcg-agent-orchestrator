public sealed class MainBranchGitRepositoryTemplateTests
{
    [Xunit.Fact]
    public void CopyTo_WarmTemplates_LaunchesNoGitAndPreservesRepositoryShape()
    {
        var warmPlain = ConductorDriverTests.CreateTempDirectory();
        var warmSkill = ConductorDriverTests.CreateTempDirectory();
        var plain = ConductorDriverTests.CreateTempDirectory();
        var skill = ConductorDriverTests.CreateTempDirectory();
        try
        {
            MainBranchGitRepositoryTemplate.CopyTo(warmPlain, includeSkillCatalog: false);
            MainBranchGitRepositoryTemplate.CopyTo(warmSkill, includeSkillCatalog: true);

            using (var launches = InfrastructureTestSupport.CountGitProbeLaunches())
            {
                MainBranchGitRepositoryTemplate.CopyTo(plain, includeSkillCatalog: false);
                Assert.Equal(0, launches.Count);
                MainBranchGitRepositoryTemplate.CopyTo(skill, includeSkillCatalog: true);
                Assert.Equal(0, launches.Count);
            }

            AssertRepositoryShape(plain);
            AssertRepositoryShape(skill);
            Assert.False(Directory.Exists(Path.Combine(plain, ".agents", "skills")));
            Assert.Equal("", RunGit(plain, "ls-files", ".agents/skills"));
            var skillFiles = Directory.GetFiles(Path.Combine(skill, ".agents", "skills"), "SKILL.md", SearchOption.AllDirectories);
            Assert.NotEmpty(skillFiles);
            Assert.Equal(RunGit(warmSkill, "ls-files", ".agents/skills"), RunGit(skill, "ls-files", ".agents/skills"));
            Assert.NotEqual("", RunGit(skill, "ls-files", ".agents/skills"));
            foreach (var file in skillFiles)
            {
                Assert.Equal(File.ReadAllText(Path.Combine(warmSkill, Path.GetRelativePath(skill, file))), File.ReadAllText(file));
            }
        }
        finally
        {
            DeleteDirectory(warmPlain);
            DeleteDirectory(warmSkill);
            DeleteDirectory(plain);
            DeleteDirectory(skill);
        }
    }

    [Xunit.Fact]
    public void CopyTo_AfterAnotherCopyCommitsAndBranches_PreservesTemplate()
    {
        foreach (var includeSkillCatalog in new[] { false, true })
        {
            var changed = ConductorDriverTests.CreateTempDirectory();
            var later = ConductorDriverTests.CreateTempDirectory();
            try
            {
                MainBranchGitRepositoryTemplate.CopyTo(changed, includeSkillCatalog);
                var initialHead = RunGit(changed, "rev-parse", "HEAD");
                File.WriteAllText(Path.Combine(changed, "README.md"), "changed");
                RunGit(changed, "add", ".");
                RunGit(changed, "commit", "-m", "copy change");
                RunGit(changed, "branch", "extra");
                Assert.Equal("2", RunGit(changed, "rev-list", "--count", "HEAD"));
                Assert.Equal("extra\nmain", RunGit(changed, "for-each-ref", "--format=%(refname:short)", "refs/heads").Replace("\r\n", "\n"));

                MainBranchGitRepositoryTemplate.CopyTo(later, includeSkillCatalog);

                AssertRepositoryShape(later);
                Assert.Equal(initialHead, RunGit(later, "rev-parse", "HEAD"));
                Assert.Equal("main", RunGit(later, "for-each-ref", "--format=%(refname:short)", "refs/heads"));
                Assert.Equal(includeSkillCatalog, Directory.Exists(Path.Combine(later, ".agents", "skills")));
            }
            finally
            {
                DeleteDirectory(changed);
                DeleteDirectory(later);
            }
        }
    }

    private static void AssertRepositoryShape(string root)
    {
        Assert.True(Directory.Exists(Path.Combine(root, ".git")));
        Assert.Equal("initial", File.ReadAllText(Path.Combine(root, "README.md")));
        Assert.Equal("main", RunGit(root, "branch", "--show-current"));
        Assert.Equal("1", RunGit(root, "rev-list", "--count", "HEAD"));
        Assert.Equal("initial", RunGit(root, "log", "-1", "--format=%s"));
        Assert.Equal("", RunGit(root, "status", "--porcelain"));
        Assert.Equal("test@example.com", RunGit(root, "config", "--local", "user.email"));
        Assert.Equal("Test User", RunGit(root, "config", "--local", "user.name"));
    }

    private static string RunGit(string workingDirectory, params string[] arguments)
    {
        var result = InfrastructureTestSupport.RunGitProbe(workingDirectory, arguments);
        Assert.True(result.Succeeded, result.ToString());
        return result.StandardOutput.Trim();
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
