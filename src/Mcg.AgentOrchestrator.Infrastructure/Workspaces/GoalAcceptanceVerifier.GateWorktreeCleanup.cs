using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class GoalAcceptanceVerifier
{
    private sealed record GateUntrackedSnapshot(
        string Root,
        HashSet<string> Paths,
        HashSet<string> IgnoredDirectories,
        HashSet<string> ExistingRootDirectories);

    private GateUntrackedSnapshot? CaptureGateWorktreeUntrackedSnapshot(string worktreePath, GoalId? goalId)
    {
        string? root = null;
        try
        {
            root = Path.GetFullPath(worktreePath).TrimEnd(Path.DirectorySeparatorChar);
            var gitRoot = GitCli.Run(root, "rev-parse", "--show-toplevel");
            if (!GitGateInspectionSucceeded(gitRoot))
            {
                EmitGateWorktreeSkippedIfTopLevel(root, goalId, gitRoot.Error);
                return null;
            }
            if (!SameGatePath(root, gitRoot.Output.Trim()))
                return null;

            var paths = ReadGateUntrackedPaths(root);
            var tracked = GitCli.Run(root, "ls-files", "-z", "--cached");
            var ignored = GitCli.Run(root, "ls-files", "-z", "--others", "--ignored", "--exclude-standard", "--directory");
            if (paths is null || !GitGateInspectionSucceeded(tracked) || !GitGateInspectionSucceeded(ignored))
            {
                EmitGateWorktreeCleanupLine($"GATE_WORKTREE_UNTRACKED_SKIPPED goal={goalId?.Value ?? "unknown"} reason=baseline-inspection-failed");
                return null;
            }

            paths.UnionWith(tracked.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries));
            var ignoredPaths = ignored.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            paths.UnionWith(ignoredPaths);
            return new GateUntrackedSnapshot(
                root,
                paths,
                new HashSet<string>(ignoredPaths.Where(path => path.EndsWith("/", StringComparison.Ordinal)), GatePathComparer),
                new HashSet<string>(Directory.EnumerateDirectories(root).Select(path => Path.GetFileName(path)), GatePathComparer));
        }
        catch (Exception exception)
        {
            // An incomplete baseline cannot authorize any deletion.
            if (root is not null)
                EmitGateWorktreeSkippedIfTopLevel(root, goalId, exception.Message);
            return null;
        }
    }

    private static HashSet<string>? ReadGateUntrackedPaths(string root)
    {
        var status = GitCli.Run(root, "status", "--porcelain=v1", "-z", "--untracked-files=all");
        if (!GitGateInspectionSucceeded(status))
            return null;

        var paths = new HashSet<string>(GatePathComparer);
        var entries = status.Output.Split('\0');
        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            if (entry.Length < 4 || entry[2] != ' ')
                continue;
            if (entry[0] == '?' && entry[1] == '?')
                paths.Add(entry[3..]);
            // Porcelain -z puts the original path in a second NUL field for renames and copies.
            else if (entry[0] is 'R' or 'C' || entry[1] is 'R' or 'C')
                index++;
        }

        return paths;
    }

    private void RemoveGateCreatedUntrackedPaths(
        GateUntrackedSnapshot? snapshot,
        GoalId? goalId,
        string? lastCheck)
    {
        if (snapshot is null)
            return;

        try
        {
            var current = ReadGateUntrackedPaths(snapshot.Root);
            if (current is null)
            {
                EmitGateWorktreeCleanupLine($"GATE_WORKTREE_UNTRACKED_SKIPPED goal={goalId?.Value ?? "unknown"} reason=recheck-inspection-failed");
                return;
            }

            foreach (var path in current.Except(snapshot.Paths, GatePathComparer)
                         .Where(path => !snapshot.IgnoredDirectories.Any(directory =>
                             path.StartsWith(directory, GatePathComparison))))
            {
                try
                {
                    if (path.EndsWith("/", StringComparison.Ordinal))
                        throw new IOException("nested-git-repository");

                    var fullPath = Path.GetFullPath(Path.Combine(
                        snapshot.Root, path.Replace('/', Path.DirectorySeparatorChar)));
                    if (!fullPath.StartsWith(snapshot.Root + Path.DirectorySeparatorChar, GatePathComparison))
                        throw new IOException("outside-worktree");
                    EnsureGatePathHasNoLinkedParent(snapshot.Root, fullPath);
                    if (Directory.Exists(fullPath))
                        throw new IOException("directory-entry");

                    if (!File.Exists(fullPath))
                        throw new IOException("missing-at-removal");
                    File.Delete(fullPath);
                    if (File.Exists(fullPath))
                        throw new IOException("file-still-present");
                    EmitGateWorktreeCleanupLine(
                        $"GATE_WORKTREE_UNTRACKED_REMOVED goal={goalId?.Value ?? "unknown"} " +
                        $"path={QuoteProgressToken(path)} check={QuoteProgressToken(lastCheck ?? "unknown")}");
                    try
                    {
                        PruneGateEmptyParents(snapshot.Root, Path.GetDirectoryName(fullPath)!, snapshot.ExistingRootDirectories);
                    }
                    catch
                    {
                        // The file is already removed; a non-empty or locked parent is harmless.
                    }
                }
                catch (Exception exception)
                {
                    EmitGateWorktreeCleanupLine(
                        $"GATE_WORKTREE_UNTRACKED_RETAINED goal={goalId?.Value ?? "unknown"} " +
                        $"path={QuoteProgressToken(path)} reason={QuoteProgressToken(exception.Message)}");
                }
            }
        }
        catch (Exception exception)
        {
            // Gate cleanup is diagnostic and cannot change the verdict or mask a gate exception.
            EmitGateWorktreeCleanupLine($"GATE_WORKTREE_UNTRACKED_SKIPPED goal={goalId?.Value ?? "unknown"} reason={QuoteProgressToken(exception.Message)}");
        }
    }

    private void EmitGateWorktreeSkippedIfTopLevel(string root, GoalId? goalId, string reason)
    {
        try
        {
            if (File.Exists(Path.Combine(root, ".git")) || Directory.Exists(Path.Combine(root, ".git")))
                EmitGateWorktreeCleanupLine($"GATE_WORKTREE_UNTRACKED_SKIPPED goal={goalId?.Value ?? "unknown"} reason={QuoteProgressToken(reason)}");
        }
        catch
        {
            // Diagnostic output must not change the gate verdict.
        }
    }

    private static bool GitGateInspectionSucceeded(GitCli.GitResult result) =>
        result.Succeeded && !result.DrainTimedOut;

    private void EmitGateWorktreeCleanupLine(string line)
    {
        try
        {
            Console.WriteLine(line);
            Console.Out.Flush();
            _testOverrides.OnGateWorktreeCleanupLineForTests?.Invoke(line);
        }
        catch
        {
            // A broken diagnostic sink cannot change the gate result.
        }
    }

    private static void EnsureGatePathHasNoLinkedParent(string root, string fullPath)
    {
        for (var parent = Path.GetDirectoryName(fullPath);
             parent is not null && !SameGatePath(parent, root);
             parent = Path.GetDirectoryName(parent))
        {
            if (File.GetAttributes(parent).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("linked-parent");
        }
    }

    private static void PruneGateEmptyParents(string root, string parent, HashSet<string> existingRootDirectories)
    {
        while (!SameGatePath(parent, root) &&
               !existingRootDirectories.Contains(Path.GetRelativePath(root, parent).Split(Path.DirectorySeparatorChar)[0]) &&
               !File.GetAttributes(parent).HasFlag(FileAttributes.ReparsePoint) &&
               !Directory.EnumerateFileSystemEntries(parent).Any())
        {
            Directory.Delete(parent);
            parent = Path.GetDirectoryName(parent)!;
        }
    }

    private static StringComparer GatePathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison GatePathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool SameGatePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), GatePathComparison);
}
