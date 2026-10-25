namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class WorkerDispatchReclaimResidue
{
    internal static bool IsResidue(IReadOnlyCollection<string> dispatchFileNames)
    {
        var hasCompressedLog = false;
        foreach (var name in dispatchFileNames)
        {
            if (name.EndsWith(".exit.txt", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".child-exit.json", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (name.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            {
                hasCompressedLog = true;
            }
            else if (name.EndsWith(".out.log", StringComparison.OrdinalIgnoreCase) ||
                     name.EndsWith(".err.log", StringComparison.OrdinalIgnoreCase) ||
                     Path.GetFileName(name).Contains(".log.part-", StringComparison.OrdinalIgnoreCase))
            {
                // Match StorageRetentionMaintenance.IsRawWorkerLog, including rotated parts.
                return false;
            }
        }

        return hasCompressedLog;
    }
}
