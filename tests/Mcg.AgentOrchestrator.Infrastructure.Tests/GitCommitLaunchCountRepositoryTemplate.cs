internal static class GitCommitLaunchCountRepositoryTemplate
{
    private static readonly Lazy<string> Template = new(CreateTemplate);

    internal static string TemplateDirectory => Template.Value;

    internal static void CopyTo(string repositoryDirectory)
    {
        var source = Template.Value;
        Directory.CreateDirectory(repositoryDirectory);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(repositoryDirectory, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(repositoryDirectory, Path.GetRelativePath(source, file)));
        }
    }

    private static string CreateTemplate()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-git-commit-launch-template-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        RunGit(root, "init", "--initial-branch=main");
        RunGit(root, "config", "user.email", "tests@example.com");
        RunGit(root, "config", "user.name", "Worktree Tests");
        RunGit(root, "config", "commit.gpgsign", "false");
        return root;
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var result = InfrastructureTestSupport.RunGitProbe(workingDirectory, arguments);
        if (!result.Succeeded)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed with exit {result.ExitCode}: " +
                $"stdout: {result.StandardOutput}; stderr: {result.StandardError}; {result}");
    }
}
