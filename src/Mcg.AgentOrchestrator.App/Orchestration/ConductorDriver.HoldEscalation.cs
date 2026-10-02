using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private IGoalLifecycleEventWriter? _holdEscalationEventWriter;

    internal IGoalLifecycleEventWriter? HoldEscalationEventWriter
    {
        get => _holdEscalationEventWriter ?? _cohortEventWriter;
        set => _holdEscalationEventWriter = value;
    }
}
