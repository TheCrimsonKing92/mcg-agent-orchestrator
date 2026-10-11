internal static class FailedRoundCheckpointRepositoryTemplate
{
    internal const string HooksPathSentinel = "template-sentinel-no-hooks";
    private const string CommitDate = "2026-01-01T00:00:00Z";
    internal static readonly DateTimeOffset PinnedCommitDate = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly Lazy<string> Template = new(
        CreateTemplate, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static string TemplateDirectory => Template.Value;

    internal static void CopyTo(string destinationDirectory, string hooksPath)
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

        RewriteHooksPath(Path.Combine(destinationDirectory, ".git", "config"), hooksPath);
    }

    private static void RewriteHooksPath(string configPath, string hooksPath)
    {
        var lines = File.ReadAllText(configPath).Split('\n');
        var inCore = false;
        var entryCount = 0;
        var placeholderIndex = -1;
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.StartsWith('['))
            {
                inCore = line.Equals("[core]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            var equals = line.IndexOf('=');
            if (!inCore || equals < 0 ||
                !line[..equals].Trim().Equals("hooksPath", StringComparison.OrdinalIgnoreCase))
                continue;

            entryCount++;
            if (line[(equals + 1)..].Trim().Equals(HooksPathSentinel, StringComparison.Ordinal))
                placeholderIndex = index;
        }

        if (entryCount != 1 || placeholderIndex < 0)
            throw new InvalidOperationException("Copied checkpoint repository config must contain exactly one core.hooksPath entry with the template sentinel.");

        var ending = lines[placeholderIndex].EndsWith('\r') ? "\r" : "";
        lines[placeholderIndex] = "\thooksPath = " + EncodeConfigValue(hooksPath) + ending;
        File.WriteAllText(configPath, string.Join('\n', lines));
    }

    private static string EncodeConfigValue(string value)
    {
        // Git escapes backslashes even in unquoted Windows paths; quote comment characters and edge whitespace.
        var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"")
            .Replace("\n", "\\n").Replace("\t", "\\t").Replace("\b", "\\b");
        var quote = value.Contains(';') || value.Contains('#') ||
            (value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1])));
        return quote ? "\"" + escaped + "\"" : escaped;
    }

    private static string CreateTemplate()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcg-failed-round-checkpoint-tests", "template-" + Guid.NewGuid().ToString("n"));
        try
        {
            Directory.CreateDirectory(root);
            RunGit(root, "init", "-b", "main");
            RunGit(root, "config", "user.email", "test@example.test");
            RunGit(root, "config", "user.name", "Checkpoint Test");
            RunGit(root, "config", "commit.gpgsign", "false");
            // Production Git inherits host config, so pin checkout conversion locally too.
            RunGit(root, "config", "core.autocrlf", "false");
            RunGit(root, "config", "core.hooksPath", HooksPathSentinel);
            File.WriteAllText(Path.Combine(root, "seed.txt"), "seed");
            File.WriteAllText(Path.Combine(root, ".gitignore"), "ignored.txt\n");
            RunGit(root, "add", ".");
            var commit = InfrastructureTestSupport.RunGitProbe(root, ["commit", "-m", "Seed"],
                new Dictionary<string, string>
                {
                    ["GIT_AUTHOR_DATE"] = CommitDate,
                    ["GIT_COMMITTER_DATE"] = CommitDate
                });
            if (!commit.Succeeded)
                throw new InvalidOperationException($"git commit failed with exit {commit.ExitCode}: {commit.StandardError}; {commit}");
            return root;
        }
        catch (Exception exception)
        {
            DeleteDirectory(root);
            throw new InvalidOperationException("Failed-round checkpoint git repository template could not be created.", exception);
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
            if (!Directory.Exists(path))
                return;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
