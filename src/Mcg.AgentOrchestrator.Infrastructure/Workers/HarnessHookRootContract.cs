using System.Diagnostics;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class HarnessHookRootContract
{
    internal const string EnvironmentVariableName = "CLAUDE_PROJECT_DIR";

    internal static string Apply(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        var root = Resolve(startInfo.WorkingDirectory);
        startInfo.Environment[EnvironmentVariableName] = root;
        return root;
    }

    internal static string Resolve(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var launchDirectory = Path.GetFullPath(workingDirectory);
        if (Directory.Exists(Path.Combine(launchDirectory, ".claude")))
        {
            return Normalize(launchDirectory);
        }

        var repositoryRoot = FindRepositoryRoot(launchDirectory);
        if (repositoryRoot is null)
        {
            return Normalize(launchDirectory);
        }

        for (var candidate = new DirectoryInfo(launchDirectory); candidate is not null; candidate = candidate.Parent)
        {
            if (Directory.Exists(Path.Combine(candidate.FullName, ".claude")))
            {
                return Normalize(candidate.FullName);
            }

            if (candidate.FullName.Equals(repositoryRoot, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        return Normalize(repositoryRoot);
    }

    internal static string? DescribeUnsupported(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var nativeRoot = root.Replace('/', Path.DirectorySeparatorChar);
        var claudeDirectory = Path.Combine(nativeRoot, ".claude");
        if (!Directory.Exists(claudeDirectory))
        {
            // A repository without hook configuration has nothing to invoke. Diagnostics begin
            // only once the repository opts into the .claude hook contract.
            return null;
        }

        string[] requiredPaths =
        [
            Path.Combine(claudeDirectory, "settings.json"),
            Path.Combine(claudeDirectory, "hooks", "Emit-Timestamp.ps1"),
            Path.Combine(claudeDirectory, "hooks", "Block-CompoundShell.ps1")
        ];
        var missingPath = requiredPaths.FirstOrDefault(path => !File.Exists(path));
        return missingPath is null
            ? null
            : $"Repository hook root contract unsupported: {EnvironmentVariableName}='{root}', but required path '{missingPath}' was not found. Launch the harness from a worktree containing .claude/settings.json and its repository hook scripts.";
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).Replace('\\', '/');

    private static string? FindRepositoryRoot(string launchDirectory)
    {
        for (var candidate = new DirectoryInfo(launchDirectory); candidate is not null; candidate = candidate.Parent)
        {
            var marker = Path.Combine(candidate.FullName, ".git");
            if (Directory.Exists(marker) || File.Exists(marker))
            {
                return candidate.FullName;
            }
        }

        return null;
    }
}
