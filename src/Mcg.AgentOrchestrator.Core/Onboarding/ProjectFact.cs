namespace Mcg.AgentOrchestrator.Core;

/// <summary>A value together with the evidence and certainty supporting it.</summary>
public sealed record ProjectFact<T>
{
    public T Value { get; }
    public FactSource Source { get; }
    public FactConfidence Confidence { get; }

    public ProjectFact(T value, FactSource source, FactConfidence confidence)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!Enum.IsDefined(confidence))
            throw new ArgumentOutOfRangeException(nameof(confidence));

        Value = value;
        Source = source;
        Confidence = confidence;
    }
}
