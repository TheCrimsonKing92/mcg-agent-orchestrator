namespace Mcg.AgentOrchestrator.App.Simulation;

internal sealed record BoardCalibrationReport(int RecordedCount, int SimulatedCount, int CountDelta,
    IReadOnlyList<LandingDelta> Matched, IReadOnlyList<string> RecordedOnly,
    IReadOnlyList<string> SimulatedOnly, double MaxAbsDeltaMinutes, double MedianAbsDeltaMinutes,
    string Verdict);
