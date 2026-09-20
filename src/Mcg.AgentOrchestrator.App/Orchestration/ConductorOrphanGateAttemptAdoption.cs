namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>
/// Decides whether a successor conductor generation should adopt a gate attempt that a previous
/// generation started, rather than treating it as dead and starting a second attempt on the same
/// candidate. Starting a second attempt spends the acceptance retry budget twice for one candidate.
///
/// The predicate keys on the <em>launching generation's</em> liveness rather than on heartbeat
/// freshness. Heartbeat freshness is not a stable orphan signal across a renewal: the successor only
/// refreshes the heartbeat when its own tick runs, so an orphan can look stale between ticks whose
/// interval exceeds the recent-heartbeat grace. Whether the generation that launched the attempt is
/// still alive does not flicker that way.
/// </summary>
internal static class ConductorOrphanGateAttemptAdoption
{
    /// <summary>
    /// True when <paramref name="attempt"/> is an orphan of another generation whose gate child is
    /// still alive, so the current generation must adopt it and must not launch a replacement.
    ///
    /// An attempt with no recorded generation predates generation identity. It is treated as
    /// unknown-but-not-mine and adopted while its child is alive, which is strictly safer than
    /// relaunching. When the gate child is dead this returns false, and the existing dead-owner path
    /// fences the attempt and charges the budget once, which is correct.
    /// </summary>
    internal static bool ShouldAdopt(
        ConductorParallelAcceptanceAttempt attempt,
        int currentGenerationId,
        Func<int, bool> isProcessAlive,
        Func<string, bool> artifactExists)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(isProcessAlive);
        ArgumentNullException.ThrowIfNull(artifactExists);

        return attempt.Outcome == ConductorParallelAcceptanceAttemptOutcome.Running &&
               attempt.ReconciledAt is null &&
               !artifactExists(attempt.ResultPath) &&
               !artifactExists(attempt.ExitCodePath) &&
               attempt.OwnerProcessId > 0 &&
               isProcessAlive(attempt.OwnerProcessId) &&
               IsForeignGeneration(attempt, currentGenerationId, isProcessAlive);
    }

    /// <summary>
    /// True when the current generation has not already recorded itself as this attempt's adopter,
    /// so the adoption is written exactly once per generation rather than on every tick.
    /// </summary>
    internal static bool NeedsAdoptionRecord(ConductorParallelAcceptanceAttempt attempt, int currentGenerationId)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        return attempt.AdoptedByGenerationId != currentGenerationId;
    }

    // Stable across every tick of the adopting generation: adopting does not make the attempt mine,
    // so the fence that keeps Launch unreachable must not be consumed by the first adoption.
    private static bool IsForeignGeneration(
        ConductorParallelAcceptanceAttempt attempt,
        int currentGenerationId,
        Func<int, bool> isProcessAlive) =>
        // A null launching generation was written before generation identity existed: unknown, not mine.
        attempt.ConductorGenerationId is not { } launchingGeneration ||
        (launchingGeneration != currentGenerationId && !isProcessAlive(launchingGeneration));
}
