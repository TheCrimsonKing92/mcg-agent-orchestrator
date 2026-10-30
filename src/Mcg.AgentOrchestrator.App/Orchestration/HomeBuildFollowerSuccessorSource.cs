namespace Mcg.AgentOrchestrator.App.Orchestration;

// A follower can retain home output and run its own store upgrade; it cannot stage or build output.
internal sealed class HomeBuildFollowerSuccessorSource(string homeRoot)
{
    internal bool IsEligible(ConductorPreparedSuccessor successor, ConductorSupervisorBuildIdentity running, string? blockedCommit)
    {
        var candidate = new HomeBuildAdoptionDetector(homeRoot, running, blockedCommit).Detect();
        return candidate is not null && candidate.AdoptedCommit == successor.RepositoryHead &&
            HomeBuildAdoptionRecord.SamePath(candidate.AppDllPath, successor.AppDllPath);
    }

    internal ConductorPreparedSuccessor? Prepare(HomeBuildAdoptionRecord requested,
        ConductorSupervisorBuildIdentity running, string? blockedCommit,
        Action<string, string, CancellationToken> upgrade, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = new HomeBuildAdoptionDetector(homeRoot, running, blockedCommit).Detect();
        if (current is null || current.Sequence < requested.Sequence) return null;
        var lease = new FileStream(current.AppDllPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            // Check authority and identity again while retirement is excluded by the DLL lease.
            var rechecked = new HomeBuildAdoptionDetector(homeRoot, running, blockedCommit).Detect();
            if (rechecked != current) { lease.Dispose(); return null; }
            upgrade(current.AppDllPath, current.AdoptedCommit, cancellationToken);
            return new ConductorPreparedSuccessor(current.RunDirectory, current.AppDllPath,
                current.AdoptedCommit, current.AdoptedCommit, "home-build-adopted", lease);
        }
        catch { lease.Dispose(); throw; }
    }
}
