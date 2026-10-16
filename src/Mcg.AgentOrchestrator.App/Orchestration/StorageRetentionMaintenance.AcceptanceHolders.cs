using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static partial class StorageRetentionMaintenance
{
    internal static bool AcceptanceHolderDirectoryPreventsRetention(string directory,
        AcceptanceHolderLease? holderLease = null) =>
        AcceptanceHolderDirectoryPreventsRetention(directory, out _, holderLease);

    private static bool AcceptanceHolderDirectoryPreventsRetention(string directory,
        out string? invalidReason, AcceptanceHolderLease? holderLease = null)
    {
        invalidReason = null;
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.attempt.json"))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (AcceptanceHolderPreventsRetention(document.RootElement, holderLease)) return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
            InvalidDataException or InvalidOperationException or FormatException)
        { invalidReason = $"attempt-metadata-unreadable:{ex.GetType().Name}"; return true; }
    }

    internal static bool AcceptanceHolderPreventsRetention(JsonElement metadata,
        AcceptanceHolderLease? holderLease = null)
    {
        if (metadata.TryGetProperty("holderContractVersion", out var version) && version.GetInt32() == 1)
        {
            var attempt = metadata.Deserialize<ConductorParallelAcceptanceAttempt>(
                new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidDataException("Acceptance retention metadata is empty.");
            return (holderLease ?? new AcceptanceHolderLease()).Observe(
                AcceptanceHolderBinding.For(attempt)).ReservesCapacity;
        }
        if (!metadata.TryGetProperty("executionProtocol", out var protocol) ||
            protocol.GetString() != "out-of-process") return false;
        var legacy = metadata.Deserialize<ConductorParallelAcceptanceAttempt>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Legacy acceptance retention metadata is empty.");
        // Retention uses the same explicit proof as admission, after terminal publication.
        return legacy.Outcome == ConductorParallelAcceptanceAttemptOutcome.Running ||
            !(LegacyAcceptanceHolderSettlement.HasPidAbsentTerminalReceipt(legacy) ||
              LegacyAcceptanceHolderSettlement.HasTerminalReceipt(AcceptanceHolderBinding.For(legacy),
                legacy.OwnerProcessId, legacy.OwnerProcessStartedAt, legacy.OwnerExecutablePath, legacy.StartedAt) &&
              LegacyAcceptanceHolderSettlement.IsAbsent(legacy.OwnerProcessId,
                legacy.OwnerProcessStartedAt, legacy.OwnerExecutablePath,
                () => AcceptanceHolderIdentity.Classify(legacy.OwnerProcessId,
                    legacy.OwnerProcessStartedAt, legacy.OwnerExecutablePath)) ||
              LegacyAcceptanceHolderSettlement.IsSettled(AcceptanceHolderBinding.For(legacy),
                legacy.OwnerProcessId, legacy.OwnerProcessStartedAt, legacy.OwnerExecutablePath, legacy.StartedAt,
                () => AcceptanceHolderIdentity.Classify(legacy.OwnerProcessId,
                    legacy.OwnerProcessStartedAt, legacy.OwnerExecutablePath), legacy.LastHeartbeatAt));
    }
}
