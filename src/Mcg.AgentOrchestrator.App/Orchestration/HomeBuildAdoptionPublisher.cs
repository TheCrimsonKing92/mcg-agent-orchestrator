namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class HomeBuildAdoptionPublisher(string homeRoot, string recordPath)
{
    internal void Record(string status, ConductorActivationBuild build)
    {
        if (status is not ("adopted" or "reverted")) return;
        var previous = HomeBuildAdoptionRecord.Read(recordPath);
        if (status == "reverted")
        {
            if (previous is not null && string.Equals(previous.AdoptedCommit, build.CommitSha, StringComparison.OrdinalIgnoreCase))
                HomeBuildAdoptionRecord.Write(recordPath, previous with { Status = "reverted" });
            return;
        }
        if (build.CommandPrefix is not { Count: >= 2 } || !HomeBuildAdoptionDetector.IsCommit(build.CommitSha)) return;
        var record = new HomeBuildAdoptionRecord(1, Path.GetFullPath(homeRoot),
            checked((previous?.Sequence ?? 0) + 1), build.CommitSha,
            build.StagedBuildId, build.CommandPrefix[1], "adopted");
        // Publish the identity first, so a newly started follower always knows its running sequence.
        HomeBuildAdoptionRecord.Write(HomeBuildAdoptionRecord.BuildIdentityPath(recordPath, build.CommitSha), record);
        HomeBuildAdoptionRecord.Write(recordPath, record);
    }
}
