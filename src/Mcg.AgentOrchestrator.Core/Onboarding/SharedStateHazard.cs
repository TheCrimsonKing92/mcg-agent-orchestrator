namespace Mcg.AgentOrchestrator.Core;

/// <summary>A unit's shared-state declaration and the resource key established by its evidence.</summary>
public sealed record SharedStateHazard
{
    public string UnitId { get; }
    public string Name { get; }
    public ProjectFact<string> IsolationKey { get; }

    public SharedStateHazard(string unitId, string name, ProjectFact<string> isolationKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unitId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(isolationKey);
        UnitId = unitId;
        Name = name;
        IsolationKey = isolationKey;
    }
}
