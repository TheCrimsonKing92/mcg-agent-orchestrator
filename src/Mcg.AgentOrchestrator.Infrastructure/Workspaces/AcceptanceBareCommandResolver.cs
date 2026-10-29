namespace Mcg.AgentOrchestrator.Infrastructure;

// Resolves only bare Windows command names, using the environment and directory of the child.
internal static class AcceptanceBareCommandResolver
{
    internal static string? Resolve(
        string command,
        IEnumerable<KeyValuePair<string, string?>> environment,
        string workingDirectory)
    {
        if (string.IsNullOrEmpty(command) || command.Contains('/') || command.Contains('\\') ||
            Path.HasExtension(command))
            return null;

        string? path = null;
        string? pathExt = null;
        foreach (var pair in environment)
        {
            if (pair.Key.Equals("PATH", StringComparison.OrdinalIgnoreCase)) path = pair.Value;
            if (pair.Key.Equals("PATHEXT", StringComparison.OrdinalIgnoreCase)) pathExt = pair.Value;
        }
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(pathExt)) return null;

        var extensions = pathExt.Split(';').Where(extension => extension.Length > 0).ToArray();
        foreach (var entry in path.Split(';'))
        {
            var directory = entry;
            if (directory.Length >= 2 && directory[0] == '"' && directory[^1] == '"')
                directory = directory[1..^1];
            foreach (var extension in extensions)
            {
                try
                {
                    var fullDirectory = directory.Length == 0
                        ? Path.GetFullPath(workingDirectory)
                        : Path.GetFullPath(directory, workingDirectory);
                    var candidate = Path.GetFullPath(Path.Combine(fullDirectory, command + extension));
                    if (File.Exists(candidate)) return candidate;
                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
                {
                    // An unusable PATH entry must not prevent searching the remaining candidates.
                }
            }
        }
        return null;
    }
}
