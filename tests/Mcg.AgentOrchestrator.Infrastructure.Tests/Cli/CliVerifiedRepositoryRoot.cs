using System.Runtime.CompilerServices;

internal static class CliVerifiedRepositoryRoot
{
    internal const string VariableName = "MCG_ORCHESTRATOR_REPOSITORY_ROOT";

    internal static bool TryGetVerifiedRoot(out string root) =>
        TryGetVerifiedRoot(Environment.GetEnvironmentVariable(VariableName), out root);

    internal static bool TryGetVerifiedRoot(string? candidate, out string root)
    {
        root = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate))
            return false;

        try
        {
            var fullPath = Path.GetFullPath(candidate);
            var gitMarker = Path.Combine(fullPath, ".git");
            if (!Directory.Exists(fullPath) ||
                (!Directory.Exists(gitMarker) && !File.Exists(gitMarker)) ||
                !File.Exists(Path.Combine(fullPath, "Mcg.AgentOrchestrator.sln")) ||
                !Directory.Exists(Path.Combine(fullPath, "tests")))
                return false;

            root = fullPath;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    internal static string Resolve(string sourceFilePath, Func<string, string?> readVariable)
    {
        if (TryGetVerifiedRoot(readVariable(VariableName), out var root))
            return root;

        for (var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFilePath)!);
             directory is not null;
             directory = directory.Parent)
        {
            var marker = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(marker) || File.Exists(marker))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException($"Could not locate repository root from source path '{sourceFilePath}'.");
    }

    internal static string Find([CallerFilePath] string sourceFilePath = "") =>
        Resolve(sourceFilePath, Environment.GetEnvironmentVariable);
}
