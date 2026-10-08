namespace Mcg.AgentOrchestrator.Infrastructure;

[Flags]
public enum UnitCommandKinds
{
    None = 0,
    Build = 1,
    Test = 2,
    All = Build | Test
}
