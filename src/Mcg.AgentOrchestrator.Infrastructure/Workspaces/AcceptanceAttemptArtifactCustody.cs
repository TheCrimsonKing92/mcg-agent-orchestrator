using System.Diagnostics;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class AcceptanceAttemptArtifactCustodyException : InvalidOperationException
{
    public AcceptanceAttemptArtifactCustodyException(string attemptId, string artifactsPath)
        : base(
            $"Artifact slot takeover refused because acceptance attempt '{attemptId}' has live custody of '{artifactsPath}'. " +
            "Wait for the acceptance attempt to reach a terminal state before retrying.")
    {
        AttemptId = attemptId;
        ArtifactsPath = artifactsPath;
    }

    public string AttemptId { get; }

    public string ArtifactsPath { get; }
}

public static class AcceptanceAttemptArtifactCustody
{
    public const string MarkerFileName = ".mcg-artifacts-custody.json";
    public const string AttemptIdVariable = "MCG_ACCEPTANCE_GATE_ATTEMPT_ID";
    public const string LivenessCheckHintVariable = "MCG_ACCEPTANCE_GATE_ATTEMPT_LIVENESS_HINT";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static void Write(
        string artifactsPath,
        string attemptId,
        string livenessCheckHint,
        int ownerProcessId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactsPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptId);
        ArgumentException.ThrowIfNullOrWhiteSpace(livenessCheckHint);

        Directory.CreateDirectory(artifactsPath);
        var marker = new CustodyMarker(
            1,
            attemptId,
            livenessCheckHint,
            ownerProcessId,
            Environment.MachineName,
            DateTimeOffset.UtcNow);
        var path = MarkerPath(artifactsPath);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(marker, JsonOptions));
        File.Move(temporaryPath, path, overwrite: true);
    }

    public static void ThrowIfLiveCustodianBlocksTakeover(
        string artifactsPath,
        string? requestingAttemptId = null)
    {
        var marker = TryRead(artifactsPath);
        if (marker is null ||
            (!string.IsNullOrWhiteSpace(requestingAttemptId) &&
             marker.AttemptId.Equals(requestingAttemptId, StringComparison.Ordinal)) ||
            !IsLive(marker))
        {
            return;
        }

        throw new AcceptanceAttemptArtifactCustodyException(marker.AttemptId, artifactsPath);
    }

    public static void Release(string artifactsPath, string attemptId)
    {
        var marker = TryRead(artifactsPath);
        if (marker is null || !marker.AttemptId.Equals(attemptId, StringComparison.Ordinal))
        {
            return;
        }

        File.Delete(MarkerPath(artifactsPath));
    }

    public static void ReleaseStableSlots(string attemptId)
    {
        for (var slotIndex = 0; slotIndex < DotnetBuildEnvironmentManager.StableSlotCount; slotIndex++)
        {
            Release(DotnetBuildEnvironmentManager.StableSlotArtifactsPath(slotIndex), attemptId);
        }
    }

    internal static string MarkerPath(string artifactsPath) =>
        Path.Combine(artifactsPath, MarkerFileName);

    private static CustodyMarker? TryRead(string artifactsPath)
    {
        var path = MarkerPath(artifactsPath);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<CustodyMarker>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static bool IsLive(CustodyMarker marker)
    {
        try
        {
            if (File.Exists(marker.LivenessCheckHint))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(marker.LivenessCheckHint));
                var root = document.RootElement;
                if (root.TryGetProperty("attemptId", out var attemptId) &&
                    attemptId.ValueKind == JsonValueKind.String &&
                    !string.Equals(attemptId.GetString(), marker.AttemptId, StringComparison.Ordinal))
                {
                    return false;
                }

                if (root.TryGetProperty("outcome", out var outcome) &&
                    outcome.ValueKind == JsonValueKind.String &&
                    !string.Equals(outcome.GetString(), "running", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var ownerProcessId = root.TryGetProperty("ownerProcessId", out var ownerProcess) &&
                    ownerProcess.TryGetInt32(out var persistedOwnerProcessId)
                        ? persistedOwnerProcessId
                        : marker.OwnerProcessId;
                return !marker.MachineName.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) ||
                    IsProcessAlive(ownerProcessId);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A live owner remains protected while its atomic lifecycle record is briefly unavailable.
        }

        return !marker.MachineName.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase) ||
            IsProcessAlive(marker.OwnerProcessId);
    }

    private static bool IsProcessAlive(int processId)
    {
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (
            ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private sealed record CustodyMarker(
        int Version,
        string AttemptId,
        string LivenessCheckHint,
        int OwnerProcessId,
        string MachineName,
        DateTimeOffset AcquiredAt);
}
