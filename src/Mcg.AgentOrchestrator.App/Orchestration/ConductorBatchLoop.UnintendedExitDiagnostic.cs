using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private string? _unintendedExitDiagnosticPath;
    private string? _unintendedExitOutputLogPath;

    internal ConductorBatchLoop WithUnintendedExitDiagnostics(string? diagnosticPath, string? outputLogPath)
    {
        _unintendedExitDiagnosticPath = diagnosticPath;
        _unintendedExitOutputLogPath = outputLogPath;
        return this;
    }

    private string RecordUnintendedExitDiagnostic(int tick, Exception exception)
    {
        string payload;
        try
        {
            payload = $"UNINTENDED_EXIT tick={tick} at={_utcNow():O}{Environment.NewLine}{exception}{Environment.NewLine}";
        }
        catch (Exception diagnosticException)
        {
            TryWriteAbnormalExitDiagnosticFailure("format", diagnosticException);
            return "stdout";
        }

        try
        {
            Console.Error.Write(payload);
        }
        catch (Exception diagnosticException)
        {
            TryWriteAbnormalExitDiagnosticFailure("stderr", diagnosticException);
        }

        if (!string.IsNullOrWhiteSpace(_unintendedExitDiagnosticPath))
        {
            try
            {
                File.AppendAllText(_unintendedExitDiagnosticPath, payload, Encoding.UTF8);
                return _unintendedExitDiagnosticPath;
            }
            catch (Exception diagnosticException)
            {
                TryWriteAbnormalExitDiagnosticFailure("file", diagnosticException);
            }
        }

        // Keep the line-oriented output log parseable when the dedicated file is unavailable.
        try
        {
            var oneLinePayload = _unintendedExitOutputLogPath is null
                ? payload.Replace(' ', '_').Replace("\r", "\\r").Replace("\n", "\\n")
                : JsonSerializer.Serialize(payload);
            EmitProgress($"LOOP_STOP_DIAGNOSTIC tick={tick} payload={oneLinePayload}");
            return _unintendedExitOutputLogPath ?? "stdout";
        }
        catch (Exception diagnosticException)
        {
            TryWriteAbnormalExitDiagnosticFailure("output", diagnosticException);
            return "stderr";
        }
    }
}
