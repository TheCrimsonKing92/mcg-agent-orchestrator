internal static class SeededGitRepositoryTemplate
{
    private static readonly Lazy<string> Template = new(CreateTemplate, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static string TemplatePath => Template.Value;

    internal static string CreateCopy()
    {
        var source = TemplatePath;
        var root = Path.Combine(Path.GetTempPath(), "mcg-gitcli-tests", Guid.NewGuid().ToString("n"));
        try
        {
            Directory.CreateDirectory(root);
            foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(root, Path.GetRelativePath(source, directory)));
            }

            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                File.Copy(file, Path.Combine(root, Path.GetRelativePath(source, file)));
            }

            return root;
        }
        catch
        {
            DeleteDirectory(root);
            throw;
        }
    }

    private static string CreateTemplate()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-gitcli-tests", "template-" + Guid.NewGuid().ToString("n"));
        try
        {
            Directory.CreateDirectory(root);
            RunGit(root, "init");
            RunGit(root, "config", "user.email", "tests@example.com");
            RunGit(root, "config", "user.name", "GitCli Tests");
            File.WriteAllText(Path.Combine(root, "seed.txt"), "seed");
            RunGit(root, "add", "-A");
            RunGit(root, "commit", "-m", "Seed");
            return root;
        }
        catch (Exception exception)
        {
            DeleteDirectory(root);
            throw new InvalidOperationException("Seeded git repository template could not be created.", exception);
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
