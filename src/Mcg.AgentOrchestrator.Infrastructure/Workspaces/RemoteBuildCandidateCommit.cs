namespace Mcg.AgentOrchestrator.Infrastructure;

// An unreferenced commit transports exactly the digest's candidate without modifying its checkout.
internal static class RemoteBuildCandidateCommit
{
    internal static bool TryCreate(string worktreeRoot,
        Func<string, Dictionary<string, string>?, string[], WorktreeTreeDigestGitStep> git,
        out string commitSha, out string treeSha, out string reason)
    {
        commitSha = treeSha = "";
        reason = "candidate-unavailable";
        var temporary = Path.Combine(OrchestratorTempRoot.GetPurposeDirectory("remote-build-candidate"), Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(temporary);
            var environment = new Dictionary<string, string>
            {
                ["GIT_INDEX_FILE"] = Path.Combine(temporary, "index"), ["GIT_OPTIONAL_LOCKS"] = "0",
                ["GIT_AUTHOR_NAME"] = "mcg remote build", ["GIT_AUTHOR_EMAIL"] = "remote-build@mcg.invalid",
                ["GIT_COMMITTER_NAME"] = "mcg remote build", ["GIT_COMMITTER_EMAIL"] = "remote-build@mcg.invalid"
            };
            string Run(params string[] args)
            {
                var step = git(worktreeRoot, environment, args);
                if (step.ExitCode != 0 || step.TimedOut) throw new IOException(args[0] + ": " + step.StandardErrorFirstLine);
                return step.Output.Trim();
            }
            var head = Run("rev-parse", "HEAD");
            var headTree = Run("rev-parse", "HEAD^{tree}");
            Run("read-tree", "HEAD");
            Run("add", "-A");
            treeSha = Run("write-tree");
            if (!Hash(head) || !Hash(headTree) || !Hash(treeSha)) throw new IOException("invalid-git-hash");
            commitSha = treeSha == headTree ? head : Run("commit-tree", treeSha, "-p", head, "-m", "mcg remote build candidate");
            if (!Hash(commitSha)) throw new IOException("invalid-commit-hash");
            reason = "matched";
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { reason = ex.Message; return false; }
        finally
        {
            try { if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    internal static WorktreeTreeDigestGitStep RunGit(string root, Dictionary<string, string>? environment, string[] args)
    {
        string[] flags = ["-c", "core.fsmonitor=false", "-c", "core.longpaths=true", "-c", "gc.auto=0", "-c", "maintenance.auto=false"];
        var result = GitCli.RunWithEnvironment(root, environment ?? new(), flags.Concat(args).ToArray());
        return new(result.ExitCode, result.Output, result.Error.Split('\n', 2)[0], result.DrainTimedOut || !result.ProcessStarted);
    }
    private static bool Hash(string value) => value.Length is 40 or 64 && value.All(Uri.IsHexDigit);
}
