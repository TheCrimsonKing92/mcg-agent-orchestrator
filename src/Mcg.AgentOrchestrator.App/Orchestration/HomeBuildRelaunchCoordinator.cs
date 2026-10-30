namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns follower reservations and their revalidation. It never drains or interrupts managed work.
internal sealed class HomeBuildRelaunchCoordinator(
    HomeBuildAdoptionDetector detector,
    Func<ConductorSelfRelaunchRequest, ConductorSelfRelaunchResult> relaunch)
{
    private string? _held;

    internal ConductorSelfRelaunchRequest? TryReserve(int tick, Func<IReadOnlyList<string>> readBlockers)
    {
        var candidate = detector.Detect();
        if (candidate is null)
        {
            _held = null;
            return null;
        }
        var blockers = readBlockers();
        if (blockers.Count > 0)
        {
            EmitHeld(tick, candidate, blockers);
            return null;
        }
        _held = null;
        ConductorBatchLoop.EmitProgress($"LOOP_RELAUNCH_SCHEDULED tick={tick} reason=home-build-adopted commit={candidate.AdoptedCommit} sequence={candidate.Sequence}");
        return new ConductorSelfRelaunchRequest("home-build", tick, candidate);
    }

    internal ConductorSelfRelaunchResult? TryHandoff(ConductorSelfRelaunchRequest request, int tick,
        Func<IReadOnlyList<string>> readBlockers, Action awaitCanaries)
    {
        if (!Revalidate()) return null;
        awaitCanaries();
        // Waiting can yield. Re-read authority and all blockers at the action boundary.
        if (!Revalidate()) return null;
        var result = relaunch(request);
        if (result.HandedOff) return result;
        if (!result.IncumbentCanContinue)
            throw new InvalidOperationException("Home-build handoff did not confirm incumbent authority.");
        EmitHeld(tick, request.HomeBuildTarget!, [result.Reason ?? "handoff-unavailable"]);
        return null;

        bool Revalidate()
        {
            var candidate = detector.Detect();
            var blockers = readBlockers();
            if (candidate is not null && candidate == request.HomeBuildTarget && blockers.Count == 0) return true;
            if (candidate is not null && blockers.Count > 0) EmitHeld(tick, candidate, blockers);
            return false;
        }
    }

    private void EmitHeld(int tick, HomeBuildAdoptionRecord candidate, IReadOnlyList<string> blockers)
    {
        var identity = $"commit={candidate.AdoptedCommit} sequence={candidate.Sequence} blockers={string.Join(',', blockers)}";
        if (_held == identity) return;
        _held = identity;
        ConductorBatchLoop.EmitProgress($"LOOP_RELAUNCH_HELD tick={tick} reason=home-build-adopted {identity}");
    }
}
