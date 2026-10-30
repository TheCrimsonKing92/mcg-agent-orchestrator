using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceTestRunPreparation
{
    private static readonly ConcurrentDictionary<string, object> DirectoryLocks = new(StringComparer.OrdinalIgnoreCase);

    internal sealed record PublicationOperations(Action<string, string> Move, Action<TimeSpan> Delay);

    internal static void Prepare(IReadOnlyList<string> receiptPaths)
        => Prepare(receiptPaths, new PublicationOperations(
            (source, destination) => File.Move(source, destination, overwrite: true), Thread.Sleep));

    internal static void Prepare(IReadOnlyList<string> receiptPaths, PublicationOperations operations)
    {
        PublishOwnership(receiptPaths, operations);
        foreach (var path in receiptPaths)
        {
            LaneSlotTimingReader.Reset(path);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"Unable to clear stale TRX before the test run: {path}", ex);
            }

            if (File.Exists(path))
            {
                throw new IOException($"Unable to clear stale TRX before the test run: {path}");
            }
        }
    }

    private static void PublishOwnership(IEnumerable<string> receiptPaths, PublicationOperations operations)
    {
        // Direct managed-MTP execution has the same diagnostic custody contract as the
        // PowerShell runner. Publish it before launch, without relying on inherited variables.
        using var owner = Process.GetCurrentProcess();
        var ownerStartedAt = owner.StartTime.ToUniversalTime();
        foreach (var directory in receiptPaths.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("Test telemetry requires an explicit results directory.");
            var normalizedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            lock (DirectoryLocks.GetOrAdd(normalizedDirectory, _ => new object()))
            {
                Directory.CreateDirectory(normalizedDirectory);
                var sidecar = Path.Combine(normalizedDirectory, ".mtp-run-ownership.json");
                if (IsOwnedBy(sidecar, owner.Id, ownerStartedAt))
                    continue;

                var temporary = sidecar + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    File.WriteAllText(temporary, JsonSerializer.Serialize(new
                    {
                        schemaVersion = 1, machineName = Environment.MachineName,
                        ownerProcessId = owner.Id, ownerProcessStartedAt = ownerStartedAt.ToString("o"),
                        createdAt = DateTimeOffset.UtcNow, runLabel = "acceptance-test-telemetry"
                    }));
                    for (var attempt = 1; ; attempt++)
                    {
                        try
                        {
                            operations.Move(temporary, sidecar);
                            break;
                        }
                        catch (Exception ex) when (attempt < 5 && (ex is IOException or UnauthorizedAccessException))
                        {
                            operations.Delay(TimeSpan.FromMilliseconds(10 * (1 << (attempt - 1))));
                        }
                    }
                }
                finally
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
        }
    }

    private static bool IsOwnedBy(string sidecar, int ownerProcessId, DateTime ownerStartedAt)
    {
        try
        {
            if (!File.Exists(sidecar))
                return false;
            using var document = JsonDocument.Parse(File.ReadAllText(sidecar));
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("ownerProcessId", out var processId)
                && processId.ValueKind == JsonValueKind.Number
                && processId.TryGetInt32(out var parsedProcessId) && parsedProcessId == ownerProcessId
                && root.TryGetProperty("ownerProcessStartedAt", out var startedAt)
                && startedAt.ValueKind == JsonValueKind.String
                && startedAt.TryGetDateTimeOffset(out var parsedStartedAt)
                && parsedStartedAt == new DateTimeOffset(ownerStartedAt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }
}
