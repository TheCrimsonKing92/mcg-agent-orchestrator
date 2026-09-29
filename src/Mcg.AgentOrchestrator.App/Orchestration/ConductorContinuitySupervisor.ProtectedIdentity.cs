using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorContinuitySupervisor
{
    private ProtectedProcessIdentity? _supervisorProtectedIdentity;

    private void RebindProtectedIdentity(ConductorSupervisorProcessIdentity self)
    {
        var identity = new ProtectedProcessIdentity(self.ProcessId, self.StartedAt.UtcDateTime.Ticks);
        _supervisorProtectedIdentity = identity;
        if (bindProtectedIdentity is null) ProtectedProcessIdentity.Bind(identity, self.ProcessId);
        else bindProtectedIdentity(identity);
    }
}
