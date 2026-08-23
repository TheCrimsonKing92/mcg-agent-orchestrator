using System.Diagnostics;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed partial class TrialRootLease
{
    public TrialTeardownReport Destroy()
    {
        lock (_sync)
        {
            if (_teardownReport is not null)
            {
                return _teardownReport;
            }

            var started = Stopwatch.StartNew();
            var diagnostics = new List<string>();
            var observedProcessIds = new HashSet<int>();
            var jobExitConfirmed = true;
            foreach (var owned in _processes)
            {
                observedProcessIds.Add(owned.ProcessId);
                if (owned.Group.TryGetActiveProcessIds(out var activeIds))
                {
                    observedProcessIds.UnionWith(activeIds);
                }
            }

            foreach (var owned in _processes)
            {
                if (owned.Group.TryDuplicateAccountingHandle(out var waitHandle))
                {
                    using (waitHandle)
                    {
                        owned.Group.Kill();
                        if (!_jobExitWaiter.WaitForExit(waitHandle, TimeSpan.FromSeconds(5)))
                        {
                            jobExitConfirmed = false;
                            diagnostics.Add($"Owned job for root process {owned.ProcessId} did not report an empty process set.");
                        }
                    }
                }
                else
                {
                    owned.Group.Kill();
                    jobExitConfirmed = false;
                    diagnostics.Add($"Could not duplicate the owned job handle for root process {owned.ProcessId}.");
                }

                owned.Process.Dispose();
            }

            var survivors = _processInventory.FindSurvivors(observedProcessIds).Distinct().Order().ToArray();
            if (survivors.Length > 0)
            {
                diagnostics.Add($"Surviving trial processes: {string.Join(", ", survivors)}.");
            }

            var outsideWrites = DetectOutsideWrites(diagnostics);
            var rootRemoved = TryDeleteRoot(diagnostics);
            started.Stop();
            var receiptPath = Path.Combine(BaseDirectory, $"{Path.GetFileName(RootPath)}.trial-root-teardown.json");
            _teardownReport = new TrialTeardownReport(
                RootPath,
                rootRemoved,
                observedProcessIds.Order().ToArray(),
                survivors,
                jobExitConfirmed,
                outsideWrites,
                CreateDuration,
                started.Elapsed > TimeSpan.Zero ? started.Elapsed : TimeSpan.FromTicks(1),
                receiptPath,
                diagnostics);
            WriteReceipt(_teardownReport, diagnostics);
            return _teardownReport;
        }
    }

    private IReadOnlyList<string> DetectOutsideWrites(List<string> diagnostics)
    {
        var writes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var before in _protectedSnapshots)
        {
            try
            {
                var after = DisposableTrialRoot.CaptureProtectedPath(before.RootPath);
                foreach (var path in before.Files.Keys.Union(after.Files.Keys, StringComparer.OrdinalIgnoreCase))
                {
                    if (!before.Files.TryGetValue(path, out var oldValue) ||
                        !after.Files.TryGetValue(path, out var newValue) ||
                        oldValue != newValue)
                    {
                        writes.Add(path);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                writes.Add(before.RootPath);
                diagnostics.Add($"Could not re-fingerprint protected path '{before.RootPath}': {ex.Message}");
            }
        }

        return writes.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private bool TryDeleteRoot(List<string> diagnostics)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                if (!Directory.Exists(RootPath))
                {
                    return true;
                }

                foreach (var file in Directory.EnumerateFiles(RootPath, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(RootPath, recursive: true);
                return !Directory.Exists(RootPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add($"Trial root delete attempt {attempt} failed: {ex.Message}");
                if (attempt < 3)
                {
                    Thread.Sleep(100 * attempt);
                }
            }
        }

        return false;
    }

    private static void WriteReceipt(TrialTeardownReport report, List<string> diagnostics)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(report.ReceiptPath) ?? ".");
            File.WriteAllText(
                report.ReceiptPath,
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add($"Failed to write teardown receipt '{report.ReceiptPath}': {ex.Message}");
        }
    }
}
