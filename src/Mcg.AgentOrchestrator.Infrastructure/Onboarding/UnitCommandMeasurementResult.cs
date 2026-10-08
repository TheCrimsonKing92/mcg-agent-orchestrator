namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>A successful observed duration or an explicit execution failure.</summary>
public sealed record UnitCommandMeasurementResult
{
    public double? Seconds { get; }
    public string? Failure { get; }

    private UnitCommandMeasurementResult(double? seconds, string? failure)
    {
        Seconds = seconds;
        Failure = failure;
    }

    public static UnitCommandMeasurementResult Succeeded(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
            throw new ArgumentOutOfRangeException(nameof(seconds));
        return new(seconds, null);
    }

    public static UnitCommandMeasurementResult Failed(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new(null, reason);
    }
}
