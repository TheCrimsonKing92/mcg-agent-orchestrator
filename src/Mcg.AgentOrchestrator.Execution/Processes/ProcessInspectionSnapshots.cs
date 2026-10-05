namespace Mcg.AgentOrchestrator.Infrastructure;

public static class ProcessInspectionSnapshots
{
    public static ProcessCommandLineSnapshot SnapshotOperation() =>
        ProcessCommandLines.SnapshotOperation();
}
