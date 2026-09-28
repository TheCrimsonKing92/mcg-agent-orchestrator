using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal interface IConductorStewardTrackedFileLister
{
    IReadOnlyList<string> MatchingFiles(string worktree, string stem);
}

internal sealed class GitConductorStewardTrackedFileLister : IConductorStewardTrackedFileLister
{
    public IReadOnlyList<string> MatchingFiles(string worktree, string stem)
    {
        var result = GitCli.Run(worktree, "ls-files", "-z");
        if (!result.Succeeded || result.DrainTimedOut)
            throw new IOException($"git ls-files failed: {result.Error}");
        return result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(path => Path.GetFileNameWithoutExtension(path).Contains(stem, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal).Take(20).ToArray();
    }
}
