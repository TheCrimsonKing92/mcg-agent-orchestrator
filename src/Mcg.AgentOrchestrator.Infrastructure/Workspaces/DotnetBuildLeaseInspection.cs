using System.Diagnostics;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class DotnetBuildLeaseInspection
{
    internal static readonly JsonSerializerOptions LeaseMetadataJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    internal static bool PathIsUnderDirectory(string path, string directory)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return fullPath.Equals(fullDirectory, StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith(fullDirectory + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    internal static bool IsProcessRunning(
        int processId,
        Func<int, bool>? queryProcessLiveness = null)
    {
        try
        {
            if (queryProcessLiveness is not null)
            {
                return queryProcessLiveness(processId);
            }

            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // A lower-integrity caller may be unable to query a live owner. Preserve the lease
            // until the byte-range lock and process identity can be observed authoritatively.
            return true;
        }
    }

    internal static BuildLockHolder CreateProcessHolder(int processId, string commandLine, bool isOrchestratorOwned)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return new BuildLockHolder(
                processId,
                process.ProcessName,
                commandLine,
                isOrchestratorOwned,
                TryGetProcessStartTime(process));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new BuildLockHolder(processId, "unknown", commandLine, isOrchestratorOwned);
        }
    }

    internal static DateTimeOffset? TryGetProcessStartTime(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    internal static ExecutionLeaseMetadata? TryReadExecutionLeaseMetadata(string path)
    {
        var metadataPath = ExecutionLeaseMetadataPath(path);
        var sourcePath = File.Exists(metadataPath) ? metadataPath : path;
        if (!File.Exists(sourcePath))
        {
            return null;
        }

        try
        {
            var text = File.ReadAllText(sourcePath).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            if (int.TryParse(text, out var legacyProcessId))
            {
                return new ExecutionLeaseMetadata(
                    0,
                    "legacy",
                    null,
                    null,
                    legacyProcessId,
                    null,
                    DateTimeOffset.MinValue);
            }

            return JsonSerializer.Deserialize<ExecutionLeaseMetadata>(text, LeaseMetadataJsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal static string ExecutionLeaseMetadataPath(string executionLockPath) =>
        $"{executionLockPath}.owner.json";

    internal sealed record ExecutionLeaseMetadata(
        int Version,
        string LeaseId,
        string? SlotOwnerToken,
        string? ArtifactsPath,
        int OwnerProcessId,
        string? MachineName,
        DateTimeOffset AcquiredAt,
        string? HolderLabel = null);
}
