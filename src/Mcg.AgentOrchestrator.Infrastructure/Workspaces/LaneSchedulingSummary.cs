namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record LaneSchedulingSummary(int Samples, int Processes, string? RootPriority,
    string? RootThrottleMasks, int BelowNormalProcesses, int EcoProcesses, int OptedOutProcesses,
    int BackgroundMemoryProcesses, string? Lowest, string? UnavailableReason = null);
