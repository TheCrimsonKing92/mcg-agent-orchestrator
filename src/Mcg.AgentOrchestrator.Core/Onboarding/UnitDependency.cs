namespace Mcg.AgentOrchestrator.Core;

/// <summary>The dependency pair is the fact value; its source belongs to the edge.</summary>
public sealed record UnitDependency
{
    public string FromUnit { get; }
    public string ToUnit { get; }
    public FactSource Source { get; }
    public FactConfidence Confidence { get; }

    public UnitDependency(string fromUnit, string toUnit, FactSource source, FactConfidence confidence)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!Enum.IsDefined(confidence))
            throw new ArgumentOutOfRangeException(nameof(confidence));

        FromUnit = fromUnit;
        ToUnit = toUnit;
        Source = source;
        Confidence = confidence;
    }
}
