using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class DispatchProcessIdentityEvidence
{
    internal static Func<int, SpawnProcessIdentity?>? Adapt(
        Func<int, (DateTimeOffset StartedAt, string ImagePath)?>? readIdentity) =>
        readIdentity is null
            ? null
            : processId => readIdentity(processId) is { } identity
                ? new SpawnProcessIdentity(processId, identity.StartedAt, identity.ImagePath)
                : null;

    internal static IReadOnlyList<SpawnProcessIdentity> Capture(IEnumerable<int> processIds) =>
        processIds.Distinct().Select(ReadCurrent).OfType<SpawnProcessIdentity>().ToArray();

    internal static SpawnProcessIdentity? ReadCurrent(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return SpawnProcessIdentityReader.TryRead(process, out var identity) ? identity : null;
        }
        catch
        {
            return null;
        }
    }

    internal static bool IsRecordedOwnerOrUnknown(
        int processId,
        IReadOnlyList<SpawnProcessIdentity>? recordedIdentities,
        Func<int, SpawnProcessIdentity?> readCurrentIdentity)
    {
        var recorded = recordedIdentities?.FirstOrDefault(identity => identity.ProcessId == processId);
        if (recorded is null)
        {
            return true;
        }

        var current = readCurrentIdentity(processId);
        if (current is null)
        {
            return true;
        }

        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return current.StartedAt.Equals(recorded.StartedAt) &&
               string.Equals(current.ImagePath, recorded.ImagePath, pathComparison);
    }

    internal static IReadOnlyList<SpawnProcessIdentity> Read(JsonElement root)
    {
        if (!root.TryGetProperty("ownedProcessIdentities", out var identities) ||
            identities.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<SpawnProcessIdentity>();
        foreach (var item in identities.EnumerateArray())
        {
            if (!item.TryGetProperty("processId", out var processIdElement) ||
                !processIdElement.TryGetInt32(out var processId) ||
                processId <= 0 ||
                !item.TryGetProperty("startedAt", out var startedAtElement) ||
                startedAtElement.ValueKind != JsonValueKind.String ||
                !DateTimeOffset.TryParse(
                    startedAtElement.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var startedAt) ||
                !item.TryGetProperty("imagePath", out var imagePathElement) ||
                imagePathElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(imagePathElement.GetString()))
            {
                continue;
            }

            result.Add(new SpawnProcessIdentity(processId, startedAt, imagePathElement.GetString()!));
        }

        return result;
    }
}
