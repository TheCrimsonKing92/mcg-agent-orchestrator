namespace Mcg.AgentOrchestrator.Core;

/// <summary>Owner input kept separately from the discovery snapshot.</summary>
public sealed record OwnerAnswer
{
    public string FactKey { get; }
    public string Value { get; }
    public string? Reason { get; }
    public DateTimeOffset RecordedUtc { get; }

    public OwnerAnswer(string factKey, string value, string? reason, DateTimeOffset recordedUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(factKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (recordedUtc.Offset != TimeSpan.Zero)
            throw new ArgumentException("An owner answer must be recorded with a UTC offset.", nameof(recordedUtc));

        FactKey = factKey;
        Value = value;
        Reason = reason;
        RecordedUtc = recordedUtc;
    }
}
