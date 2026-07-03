using System.Text.Json;
using System.Text.Json.Serialization;

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
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    static FileDiagnosticWriter()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    private static readonly object WriteLock = new();

    public void WriteRecord(DispatchDiagnosticRecord record)
    {
        try
        {
            var logDir = Path.GetDirectoryName(record.OutputPath);
            if (string.IsNullOrEmpty(logDir))
                logDir = ".";

            Directory.CreateDirectory(logDir);
            var logPath = Path.Combine(logDir, "dispatch-diagnostics.jsonl");
            var line = JsonSerializer.Serialize(record, JsonOptions) + "\n";

            lock (WriteLock)
            {
                File.AppendAllText(logPath, line);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DispatchDiagnostic] Write failed: {ex.Message}");
        }
    }
}
