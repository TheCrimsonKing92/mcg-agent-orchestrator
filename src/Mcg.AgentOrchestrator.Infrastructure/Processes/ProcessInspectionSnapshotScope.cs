namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class ProcessInspectionSnapshotScope(Func<ProcessCommandLineSnapshot> factory)
{
    private ProcessCommandLineSnapshot? _snapshot;

    public ProcessCommandLineSnapshot Get() => _snapshot ??= factory();
}
