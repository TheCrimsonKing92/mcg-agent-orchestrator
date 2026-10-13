using System.Diagnostics;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceTestRunPreparation
{
    internal static void Prepare(IReadOnlyList<string> receiptPaths)
    {
        PublishOwnership(receiptPaths);
        foreach (var path in receiptPaths)
        {
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

    private static void PublishOwnership(IEnumerable<string> receiptPaths)
    {
        // Direct managed-MTP execution has the same diagnostic custody contract as the
        // PowerShell runner. Publish it before launch, without relying on inherited variables.
        using var owner = Process.GetCurrentProcess();
        foreach (var directory in receiptPaths.Select(Path.GetDirectoryName).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("Test telemetry requires an explicit results directory.");
            Directory.CreateDirectory(directory);
            var sidecar = Path.Combine(directory, ".mtp-run-ownership.json");
            var temporary = sidecar + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(new
                {
                    schemaVersion = 1, machineName = Environment.MachineName,
                    ownerProcessId = owner.Id, ownerProcessStartedAt = owner.StartTime.ToUniversalTime().ToString("o"),
                    createdAt = DateTimeOffset.UtcNow, runLabel = "acceptance-test-telemetry"
                }));
                File.Move(temporary, sidecar, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
