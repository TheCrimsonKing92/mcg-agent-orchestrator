namespace Mcg.AgentOrchestrator.Infrastructure;

// File-change evidence for a recorded integration; the conductor still validates resolution scope.
internal static class IntegrationMergeChangedPaths
{
    internal static IReadOnlyList<string>? Read(string worktree, Func<string, string[], GitCli.GitResult> runGit)
    {
        try
        {
            if (!ConductorConflictIntegrationMarker.TryReadRecordedHeads(worktree, out var originalHead, out var mainSha))
                return null;

            var chain = runGit(worktree, ["rev-list", "--first-parent", "--parents", $"{originalHead}..HEAD"]);
            if (!chain.Succeeded || chain.DrainTimedOut)
                return null;
            // A commit on HEAD's first-parent walk is necessarily an ancestor of HEAD.
            var integrationFound = chain.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Any(parts => parts.Length == 3 && parts[1] == originalHead && parts[2] == mainSha);
            if (!integrationFound)
                return null;

            var diff = runGit(worktree, ["diff", "--name-only", "-z", originalHead, "HEAD"]);
            if (!diff.Succeeded || diff.DrainTimedOut)
                return null;
            return diff.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { return null; }
    }
}
