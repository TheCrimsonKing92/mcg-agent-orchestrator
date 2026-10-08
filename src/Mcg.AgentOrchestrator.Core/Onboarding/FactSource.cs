namespace Mcg.AgentOrchestrator.Core;

/// <summary>A repository location or a measurement receipt, never an ambient absolute path.</summary>
public sealed record FactSource
{
    public string? Path { get; }
    public int? Line { get; }
    public string? MeasurementReference { get; }

    public FactSource(string? path = null, int? line = null, string? measurementReference = null)
    {
        var hasPath = !string.IsNullOrWhiteSpace(path);
        var hasMeasurement = !string.IsNullOrWhiteSpace(measurementReference);
        if (hasPath == hasMeasurement ||
            (hasPath && (line is null or < 1 || path!.Contains('\\') || path.Contains(':') ||
                path.StartsWith('/') || path.Split('/').Any(segment => segment is "" or "." or ".."))) ||
            (hasMeasurement && line is not null))
        {
            throw new ArgumentException("A fact source requires a repository-relative path and positive line, or a measurement reference.");
        }

        Path = hasPath ? path : null;
        Line = line;
        MeasurementReference = hasMeasurement ? measurementReference : null;
    }
}
