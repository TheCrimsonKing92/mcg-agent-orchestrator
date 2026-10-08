namespace Mcg.AgentOrchestrator.Core;

/// <summary>A declared prerequisite and its version evidence.</summary>
public sealed record EnvironmentNeed
{
    public string Name { get; }
    public ProjectFact<string> Value { get; }

    public EnvironmentNeed(string name, ProjectFact<string> value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        Name = name;
        Value = value;
    }
}
