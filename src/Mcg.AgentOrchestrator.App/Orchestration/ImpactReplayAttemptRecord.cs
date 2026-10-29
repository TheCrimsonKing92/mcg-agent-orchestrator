namespace Mcg.AgentOrchestrator.App.Orchestration;

// One historical candidate and its counterfactual lane selection; never a gate verdict.
internal sealed record ImpactReplayAttemptRecord(
    string AttemptId,
    string GoalId,
    DateTimeOffset StartedAt,
    string MainHeadSha,
    string BranchHeadSha,
    string SelectionOutcome,
    string? DegradationKind,
    string? SelectionReason,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> SelectedTestClasses,
    IReadOnlyList<string> FloorLanes,
    IReadOnlyList<string> RanLanes,
    IReadOnlyList<string> WouldRunLanes,
    IReadOnlyList<string> SkippedLanes,
    double EstimatedSerialSecondsSaved,
    IReadOnlyList<ImpactReplayLaneOutcome> LaneOutcomes,
    IReadOnlyList<ImpactReplayFailingClass> FailingClasses,
    bool MissCheckComplete,
    IReadOnlyList<string> EvidenceWarnings)
{
    public double ShareOfLanesSkipped => RanLanes.Count == 0 ? 0 : (double)SkippedLanes.Count / RanLanes.Count;
}
