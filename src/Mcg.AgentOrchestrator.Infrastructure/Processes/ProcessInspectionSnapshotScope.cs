namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class ProcessInspectionSnapshotScope(Func<ProcessCommandLineSnapshot> factory)
{
    private ProcessCommandLineSnapshot? _snapshot;

    internal ProcessCommandLineSnapshot Get() => _snapshot ??= factory();
}
