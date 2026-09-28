using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    internal string? ResolveCurrentMainHeadSha(Goal goal) => _resolveAcceptanceHeads(goal).MainHeadSha;
}
