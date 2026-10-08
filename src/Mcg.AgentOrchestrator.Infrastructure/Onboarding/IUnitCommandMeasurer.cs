namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Explicit execution boundary: measures one learned command in its repository.</summary>
public interface IUnitCommandMeasurer
{
    UnitCommandMeasurementResult Measure(string repositoryRoot, string unitId, UnitCommandKinds kind, string command);
}
