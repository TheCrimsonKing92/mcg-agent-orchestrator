namespace Mcg.AgentOrchestrator.Core;

/// <summary>Commands derived from a unit's declarations, without executing them.</summary>
public sealed record UnitCommands
{
    public string UnitId { get; }
    public ProjectFact<string> BuildCommand { get; }
    public ProjectFact<string>? TestCommand { get; }

    public UnitCommands(string unitId, ProjectFact<string> buildCommand, ProjectFact<string>? testCommand)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unitId);
        ArgumentNullException.ThrowIfNull(buildCommand);
        UnitId = unitId;
        BuildCommand = buildCommand;
        TestCommand = testCommand;
    }
}
