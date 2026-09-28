using System.Diagnostics;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class WorktreeTreeDigest
{
    internal const string Algorithm = "wbc1:git-tree";

    internal static bool TryCompute(string worktreeRoot, out string digest, out string reason)
        => TryCompute(worktreeRoot, out digest, out reason, null);

    internal static bool TryCompute(string worktreeRoot, out string digest, out string reason,
        Action<string>? temporaryRootObserver)
    {
        digest = string.Empty;
        reason = "digest-unavailable";
        var temporaryRoot = Path.Combine(OrchestratorTempRoot.GetPurposeDirectory("worktree-digest"),
            "mcg-worktree-digest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temporaryRoot);
            temporaryRootObserver?.Invoke(temporaryRoot);
            var objects = Path.Combine(temporaryRoot, "objects");
            Directory.CreateDirectory(objects);
            var commonObjects = Run(worktreeRoot, null, "rev-parse", "--path-format=absolute", "--git-path", "objects");
            if (commonObjects.ExitCode != 0 || string.IsNullOrWhiteSpace(commonObjects.Output)) return false;
            var environment = new Dictionary<string, string>
            {
                ["GIT_INDEX_FILE"] = Path.Combine(temporaryRoot, "index"),
                ["GIT_OBJECT_DIRECTORY"] = objects,
                ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = commonObjects.Output.Trim(),
                ["GIT_OPTIONAL_LOCKS"] = "0"
            };
            if (Run(worktreeRoot, environment, "read-tree", "HEAD").ExitCode != 0 ||
                Run(worktreeRoot, environment, "add", "-A").ExitCode != 0) return false;
            var tree = Run(worktreeRoot, environment, "write-tree");
            var hash = tree.Output.Trim();
            if (tree.ExitCode != 0 ||
                (hash.Length != 40 && hash.Length != 64) ||
                !hash.All(Uri.IsHexDigit)) return false;
            digest = $"{Algorithm}:{hash.ToLowerInvariant()}";
            reason = "matched";
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
        finally
        {
            try { Directory.Delete(temporaryRoot, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static (int ExitCode, string Output) Run(string root, Dictionary<string, string>? environment, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in new[] { "-c", "core.fsmonitor=false", "-c", "core.longpaths=true", "-c", "gc.auto=0", "-c", "maintenance.auto=false" }.Concat(args))
            start.ArgumentList.Add(arg);
        if (environment is not null)
            foreach (var (key, value) in environment) start.Environment[key] = value;
        using var process = Process.Start(start) ?? throw new IOException("Could not start git.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            return (-1, string.Empty);
        }
        if (!Task.WaitAll([output, error], 5_000)) return (-1, string.Empty);
        return (process.ExitCode, output.Result);
    }
}
