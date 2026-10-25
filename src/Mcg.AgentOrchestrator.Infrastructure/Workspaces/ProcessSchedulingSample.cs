namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record ProcessSchedulingSample(int ProcessId, string Image, string? PriorityClass,
    uint? ControlMask, uint? StateMask, int? MemoryPriority, string? UnavailableReason = null)
{
    internal bool IsBelowNormal => PriorityClass is "Idle" or "BelowNormal";
    internal bool IsExplicitEco => ControlMask == 1 && StateMask == 1;
    internal bool IsOptedOut => ControlMask == 1 && StateMask == 0;
    internal bool IsBackgroundMemory => MemoryPriority == 1;
}
