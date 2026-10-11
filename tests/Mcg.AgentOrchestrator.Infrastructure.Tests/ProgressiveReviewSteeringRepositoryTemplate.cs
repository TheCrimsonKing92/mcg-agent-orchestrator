internal static class ProgressiveReviewSteeringRepositoryTemplate
{
    private static readonly Lazy<string> Template = new(
        CreateTemplate, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static void CopyTo(string destinationDirectory)
    {
        var source = Template.Value;
        Directory.CreateDirectory(destinationDirectory);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destinationDirectory, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destinationDirectory, Path.GetRelativePath(source, file)), overwrite: true);
        }
    }

    private static string CreateTemplate()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-progressive-review-steering-tests", "template-" + Guid.NewGuid().ToString("n"));
        try
        {
            Directory.CreateDirectory(root);
            RunGit(root, "init");
            RunGit(root, "config", "user.email", "test@example.test");
            RunGit(root, "config", "user.name", "Test User");
            File.WriteAllText(Path.Combine(root, "README.md"), "test");
            RunGit(root, "add", "README.md");
            RunGit(root, "commit", "-m", "initial");
            return root;
        }
        catch (Exception exception)
        {
            DeleteDirectory(root);
            throw new InvalidOperationException("Progressive review steering repository template could not be created.", exception);
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
            if (!Directory.Exists(path)) return;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
