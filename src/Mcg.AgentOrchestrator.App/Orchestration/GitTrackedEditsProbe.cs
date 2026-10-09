using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GitTrackedEditsProbe
{
    // Unresolved observations never release a hold.
    internal static (bool Clean, AuthorDraftTrackedEdits? Edits) Probe(string root)
    {
        var diff = GitCli.Run(root, "diff", "--quiet", "HEAD", "--");
        if (!diff.ProcessStarted || diff.DrainTimedOut) return (false, null);
        if (diff.ExitCode == 0) return (true, null);
        if (diff.ExitCode != 1) return (false, null);
        var names = GitCli.Run(root, "diff", "--name-only", "-z", "HEAD", "--");
        if (!names.Succeeded || names.DrainTimedOut) return (false, null);
        var paths = names.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Order(StringComparer.Ordinal).ToArray();
        return paths.Length == 0 ? (false, null) : (false, new(paths));
    }
}
