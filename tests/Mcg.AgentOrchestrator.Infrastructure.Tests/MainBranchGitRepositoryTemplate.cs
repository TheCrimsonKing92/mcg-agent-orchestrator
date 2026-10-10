internal static class MainBranchGitRepositoryTemplate
{
    private static readonly Lazy<string> PlainTemplate = new(
        () => CreateTemplate(includeSkillCatalog: false), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<string> SkillTemplate = new(
        () => CreateTemplate(includeSkillCatalog: true), LazyThreadSafetyMode.ExecutionAndPublication);

    internal static void CopyTo(string destinationDirectory, bool includeSkillCatalog)
    {
        var source = (includeSkillCatalog ? SkillTemplate : PlainTemplate).Value;
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destinationDirectory, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destinationDirectory, Path.GetRelativePath(source, file)));
        }
    }

    private static string CreateTemplate(bool includeSkillCatalog)
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-conductor-driver-tests", "template-" + Guid.NewGuid().ToString("n"));
        try
        {
            Directory.CreateDirectory(root);
            if (includeSkillCatalog)
                InfrastructureTestSupport.SeedLocalSkillCatalog(root);
            RunGit(root, "init");
            RunGit(root, "checkout", "-b", "main");
            RunGit(root, "config", "user.email", "test@example.com");
            RunGit(root, "config", "user.name", "Test User");
            File.WriteAllText(Path.Combine(root, "README.md"), "initial");
            RunGit(root, "add", ".");
            RunGit(root, "commit", "-m", "initial");
            return root;
        }
        catch (Exception exception)
        {
            DeleteDirectory(root);
            throw new InvalidOperationException("Main-branch git repository template could not be created.", exception);
        }
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var result = InfrastructureTestSupport.RunGitProbe(workingDirectory, arguments);
        if (!result.Succeeded)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed with exit {result.ExitCode}: {result.StandardError}; {result}");
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
