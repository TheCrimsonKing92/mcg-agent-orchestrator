namespace Mcg.AgentOrchestrator.Core;

/// <summary>Observed durations; declarations and estimates cannot serve as measurement evidence.</summary>
public sealed record UnitMeasurement
{
    public string UnitId { get; }
    public ProjectFact<double>? BuildSeconds { get; }
    public ProjectFact<double>? TestSeconds { get; }

    public UnitMeasurement(string unitId, ProjectFact<double>? buildSeconds, ProjectFact<double>? testSeconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unitId);
        if (buildSeconds is null && testSeconds is null)
            throw new ArgumentException("A unit measurement requires at least one observed duration.");
        Validate(buildSeconds);
        Validate(testSeconds);
        UnitId = unitId;
        BuildSeconds = buildSeconds;
        TestSeconds = testSeconds;
    }

    private static void Validate(ProjectFact<double>? fact)
    {
        if (fact is not null && (fact.Source.MeasurementReference is null || !double.IsFinite(fact.Value) || fact.Value < 0))
            throw new ArgumentException("A duration requires a measurement reference and finite, non-negative seconds.");
    }
}
