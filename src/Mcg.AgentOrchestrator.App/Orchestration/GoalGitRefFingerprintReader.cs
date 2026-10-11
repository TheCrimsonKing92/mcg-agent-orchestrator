using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GoalGitRefFingerprintReader
{
    internal static string? TryRead(string executionDirectory, string integrationBranch)
    {
        try
        {
            var metadataDirectory = Path.GetFullPath(Path.Combine(executionDirectory, ".git"));
            if (!Directory.Exists(metadataDirectory))
            {
                return null;
            }

            var fingerprint = new StringBuilder();
            var head = File.ReadAllText(Path.Combine(metadataDirectory, "HEAD"));
            Append(fingerprint, "HEAD", head);
            if (head.StartsWith("ref: ", StringComparison.Ordinal))
            {
                var target = head["ref: ".Length..].Trim();
                AppendOptionalRef(fingerprint, "HEAD-target", metadataDirectory, target);
            }

            AppendOptionalRef(fingerprint, "integration", metadataDirectory, $"refs/heads/{integrationBranch}");
            var goalRefs = ResolveMetadataPath(metadataDirectory, GoalGitFactIndex.GoalBranchRefPrefix);
            Append(fingerprint, "goal-refs", Directory.Exists(goalRefs) ? "present" : "absent");
            if (Directory.Exists(goalRefs))
            {
                var files = Directory.EnumerateFiles(goalRefs, "*", SearchOption.AllDirectories)
                    .Select(path => (Path: path, Relative: Path.GetRelativePath(goalRefs, path).Replace('\\', '/')))
                    .OrderBy(item => item.Relative, StringComparer.Ordinal);
                foreach (var file in files)
                {
                    Append(fingerprint, "goal-path", file.Relative);
                    Append(fingerprint, "goal-text", File.ReadAllText(file.Path));
                }
            }

            var packedRefs = Path.Combine(metadataDirectory, "packed-refs");
            Append(fingerprint, "packed-refs", File.Exists(packedRefs)
                ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(packedRefs)))
                : "absent");

            var worktrees = Path.Combine(metadataDirectory, "worktrees");
            Append(fingerprint, "worktrees", Directory.Exists(worktrees) ? "present" : "absent");
            if (Directory.Exists(worktrees))
            {
                foreach (var entry in Directory.EnumerateDirectories(worktrees)
                    .OrderBy(Path.GetFileName, StringComparer.Ordinal))
                {
                    Append(fingerprint, "worktree-name", Path.GetFileName(entry));
                    Append(fingerprint, "worktree-gitdir", File.ReadAllText(Path.Combine(entry, "gitdir")));
                }
            }

            return fingerprint.ToString();
        }
        catch (Exception)
        {
            // Unreadable or unsafe metadata can never authorize snapshot reuse.
            return null;
        }
    }

    private static void AppendOptionalRef(
        StringBuilder fingerprint, string label, string metadataDirectory, string refName)
    {
        var path = ResolveMetadataPath(metadataDirectory, refName);
        Append(fingerprint, $"{label}-path", refName);
        var exists = File.Exists(path);
        Append(fingerprint, $"{label}-presence", exists ? "present" : "absent");
        if (exists)
        {
            Append(fingerprint, $"{label}-text", File.ReadAllText(path));
        }
    }

    private static string ResolveMetadataPath(string metadataDirectory, string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(metadataDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(metadataDirectory + Path.DirectorySeparatorChar, comparison))
        {
            throw new IOException("Reference path escapes repository metadata.");
        }

        return path;
    }

    private static void Append(StringBuilder fingerprint, string label, string value) =>
        fingerprint.Append(label).Append(':').Append(value.Length.ToString(CultureInfo.InvariantCulture))
            .Append(':').Append(value).Append('\n');
}
