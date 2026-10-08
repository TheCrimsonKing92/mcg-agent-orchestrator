using Mcg.AgentOrchestrator.Infrastructure;

internal sealed class RecordingUnitCommandMeasurer : IUnitCommandMeasurer
{
    public List<(string Root, string UnitId, UnitCommandKinds Kind, string Command)> Calls { get; } = [];
    public Func<string, UnitCommandKinds, UnitCommandMeasurementResult> Result { get; init; } =
        (_, kind) => UnitCommandMeasurementResult.Succeeded(kind == UnitCommandKinds.Build ? 12.5 : 3.25);

    public UnitCommandMeasurementResult Measure(string repositoryRoot, string unitId, UnitCommandKinds kind, string command)
    {
        Calls.Add((repositoryRoot, unitId, kind, command));
        return Result(unitId, kind);
    }
}
