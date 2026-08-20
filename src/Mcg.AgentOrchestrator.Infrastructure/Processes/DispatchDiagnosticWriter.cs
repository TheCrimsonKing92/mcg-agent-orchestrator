namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record DispatchDiagnosticRecord(
    string GoalId,
    string TaskId,
    string Prefix,
    int ExitCode,
    string OutputPath,
    bool FileExists,
    long FileLen,
    long ReadLen,
    long StderrLen,
    string Classification,
    string Reason,
    string Timestamp,
    DispatchAuthoritativeState? DispatchState = null);

public interface IDispatchDiagnosticWriter
{
    void WriteRecord(DispatchDiagnosticRecord record);
}

public sealed class FileDiagnosticWriter : IDispatchDiagnosticWriter
{
    public const string RetentionDecision =
        "Disabled: dispatch-diagnostics.jsonl has no production reader; structured task/process records remain authoritative.";

    public void WriteRecord(DispatchDiagnosticRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        // Intentionally no-op until a production consumer exists. The previous append-only file grew
        // without bound while duplicating authoritative dispatch state and had no production reader.
    }
}
