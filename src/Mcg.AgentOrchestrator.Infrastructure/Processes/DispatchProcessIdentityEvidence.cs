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

    internal static IReadOnlyList<SpawnProcessIdentity> Capture(
        IEnumerable<int> processIds,
        Func<IReadOnlyList<int>?> readCurrentOwnedProcessIds,
        Func<int, SpawnProcessIdentity?>? readCurrentIdentity = null)
    {
        var captured = processIds
            .Distinct()
            .Select(readCurrentIdentity ?? ReadCurrent)
            .OfType<SpawnProcessIdentity>()
            .ToArray();
        var currentOwnedProcessIds = readCurrentOwnedProcessIds();
        if (currentOwnedProcessIds is null)
        {
            return [];
        }

        var currentOwners = currentOwnedProcessIds.ToHashSet();
        return captured.Where(identity => currentOwners.Contains(identity.ProcessId)).ToArray();
    }

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

    internal static bool IsRecordedOwner(
        int processId,
        IReadOnlyList<SpawnProcessIdentity>? recordedIdentities,
        Func<int, SpawnProcessIdentity?> readCurrentIdentity)
    {
        var recorded = recordedIdentities?
            .Where(identity => identity.ProcessId == processId)
            .ToArray();
        if (recorded is not { Length: > 0 })
        {
            return false;
        }

        var current = readCurrentIdentity(processId);
        return recorded.Any(identity =>
            SpawnProcessIdentityReader.EvaluateRecordedIdentity(identity, current, out _) ==
            SpawnTrackedProcessStatus.LiveMatch);
    }

    internal static IReadOnlyList<int> GetLiveRecordedOwnerProcessIds(
        IEnumerable<int> candidateProcessIds,
        IReadOnlyList<SpawnProcessIdentity>? recordedIdentities,
        Func<int, bool> isProcessRunning,
        Func<int, SpawnProcessIdentity?>? readCurrentIdentity = null) =>
        candidateProcessIds
            .Where(processId => processId > 0)
            .Distinct()
            .Where(isProcessRunning)
            .Where(processId => IsRecordedOwner(
                processId,
                recordedIdentities,
                readCurrentIdentity ?? ReadCurrent))
            .ToArray();

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
