internal static class BatchLoopGitRepositoryTemplate
{
    private static readonly Lazy<string> Template = new(
        CreateTemplate, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static void CopyTo(string destinationDirectory)
    {
        var source = Template.Value;
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destinationDirectory, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destinationDirectory, Path.GetRelativePath(source, file)));
        }
    }

    private static string CreateTemplate()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-batch-loop-tests", "template-" + Guid.NewGuid().ToString("n"));
        try
        {
            Directory.CreateDirectory(root);
            RunGit(root, "init");
            RunGit(root, "checkout", "-b", "main");
            RunGit(root, "config", "user.email", "tests@example.com");
            RunGit(root, "config", "user.name", "Batch Loop Tests");
            File.WriteAllText(Path.Combine(root, "seed.txt"), "seed");
            RunGit(root, "add", "-A");
            RunGit(root, "commit", "-m", "Seed");
            return root;
        }
        catch (Exception exception)
        {
            DeleteDirectory(root);
            throw new InvalidOperationException("Batch-loop git repository template could not be created.", exception);
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
