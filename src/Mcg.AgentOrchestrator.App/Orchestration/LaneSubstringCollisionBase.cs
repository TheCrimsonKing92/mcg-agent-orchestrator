using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record LaneSubstringCollisionBase(string BaseManifestJson, string DiffText)
{
    internal static LaneSubstringCollisionBase? Read(string worktreePath, string integrationBranch)
    {
        try
        {
            var mergeBase = GitCli.Run(worktreePath, "merge-base", integrationBranch, "HEAD");
            if (!mergeBase.Succeeded || mergeBase.DrainTimedOut || !mergeBase.ProcessStarted) return null;
            var revision = mergeBase.Output.Trim();
            var manifest = GitCli.Run(worktreePath, "show", $"{revision}:config/acceptance-manifest.json");
            if (!manifest.Succeeded || manifest.DrainTimedOut || !manifest.ProcessStarted) return null;
            var diff = GitCli.Run(worktreePath, "diff", "-U0", "--no-color", "--no-ext-diff", "-M",
                revision, "HEAD", "--", "tests/");
            if (!diff.Succeeded || diff.DrainTimedOut || !diff.ProcessStarted) return null;
            return new LaneSubstringCollisionBase(manifest.Output, diff.Output);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
        {
            return null;
        }
    }
}
