using System.Diagnostics;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class WorktreeTreeDigest
{
    internal const string Algorithm = "wbc1:git-tree";

    internal static bool TryCompute(string worktreeRoot, out string digest, out string reason)
        => TryCompute(worktreeRoot, out digest, out reason, null);

    internal static bool TryCompute(string worktreeRoot, out string digest, out string reason,
        Action<string>? temporaryRootObserver)
        => TryCompute(worktreeRoot, out digest, out reason, out _, Run, temporaryRootObserver);

    internal static bool TryCompute(string worktreeRoot, out string digest, out string reason,
        out WorktreeTreeDigestFailure? failure,
        Func<string, Dictionary<string, string>?, string[], WorktreeTreeDigestGitStep>? gitRunner = null,
        Action<string>? temporaryRootObserver = null)
    {
        digest = string.Empty;
        reason = "digest-unavailable";
        failure = null;
        var run = gitRunner ?? Run;
        string? temporaryRoot = null;
        try
        {
            temporaryRoot = Path.Combine(OrchestratorTempRoot.GetPurposeDirectory("worktree-digest"),
                "mcg-worktree-digest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryRoot);
            temporaryRootObserver?.Invoke(temporaryRoot);
            var objects = Path.Combine(temporaryRoot, "objects");
            Directory.CreateDirectory(objects);
            var commonObjects = run(worktreeRoot, null, ["rev-parse", "--path-format=absolute", "--git-path", "objects"]);
            if (commonObjects.ExitCode != 0 || commonObjects.TimedOut || string.IsNullOrWhiteSpace(commonObjects.Output))
            {
                failure = WorktreeTreeDigestFailure.FromStep("rev-parse", commonObjects);
                return false;
            }
            var environment = new Dictionary<string, string>
            {
                ["GIT_INDEX_FILE"] = Path.Combine(temporaryRoot, "index"),
                ["GIT_OBJECT_DIRECTORY"] = objects,
                ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = commonObjects.Output.Trim(),
                ["GIT_OPTIONAL_LOCKS"] = "0"
            };
            foreach (var args in new[] { new[] { "read-tree", "HEAD" }, new[] { "add", "-A" }, new[] { "write-tree" } })
            {
                var result = run(worktreeRoot, environment, args);
                if (result.ExitCode != 0 || result.TimedOut)
                {
                    failure = WorktreeTreeDigestFailure.FromStep(args[0], result);
                    return false;
                }
                if (args[0] != "write-tree") continue;
                var hash = result.Output.Trim();
                if ((hash.Length != 40 && hash.Length != 64) || !hash.All(Uri.IsHexDigit))
                {
                    failure = WorktreeTreeDigestFailure.FromStep("tree-hash", result);
                    return false;
                }
                digest = $"{Algorithm}:{hash.ToLowerInvariant()}";
            }
            reason = "matched";
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            failure = WorktreeTreeDigestFailure.FromException(exception);
            return false;
        }
        finally
        {
            try { if (temporaryRoot is not null) Directory.Delete(temporaryRoot, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static WorktreeTreeDigestGitStep Run(string root, Dictionary<string, string>? environment, params string[] args)
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
        return RunProcess(start, 60_000, PipeDrain.DefaultTimeoutMilliseconds);
    }

    internal static WorktreeTreeDigestGitStep RunProcess(
        ProcessStartInfo start, int processTimeoutMilliseconds, int drainTimeoutMilliseconds)
    {
        using var process = Process.Start(start) ?? throw new IOException("Could not start git.");
        // Dedicated drains keep output independent of thread-pool availability without blocking the exit wait.
        var outputDrain = PipeDrain.Start(process.StandardOutput, "digest-stdout-drain");
        var errorDrain = PipeDrain.Start(process.StandardError, "digest-stderr-drain");
        if (!process.WaitForExit(processTimeoutMilliseconds))
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            return new(-1, string.Empty,
                WorktreeTreeDigestFailure.FirstLine(errorDrain.Text), true, TimeoutKind: "wait");
        }
        var drainDeadline = Environment.TickCount64 + drainTimeoutMilliseconds;
        var outputDrained = outputDrain.Join(drainDeadline);
        var errorDrained = errorDrain.Join(drainDeadline);
        if (!outputDrained || !errorDrained)
            return new(-1, string.Empty,
                WorktreeTreeDigestFailure.FirstLine(errorDrain.Text), true, TimeoutKind: "drain");
        return new(process.ExitCode, outputDrain.Text, WorktreeTreeDigestFailure.FirstLine(errorDrain.Text), false);
    }
}
