namespace Mcg.AgentOrchestrator.Infrastructure;

// Observation only: these processes remain in their owned group for accounting and cleanup.
internal static class ProcessObservationRoles
{
    internal static IReadOnlyList<int> CommandCandidates(
        IReadOnlyList<int> ownedProcessIds,
        IReadOnlyDictionary<int, ProcessInspectionRecord> records) =>
        ownedProcessIds.Where(pid =>
            !records.TryGetValue(pid, out var record) ||
            record.Status != ProcessInspectionStatus.Available ||
            record.StartedAt is null ||
            !IsWindowsConsoleInfrastructure(record.ExecutablePath)).ToArray();

    internal static bool IsWindowsConsoleInfrastructure(string? executablePath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(executablePath))
            return false;

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return string.Equals(executablePath, Path.Combine(windows, "System32", "conhost.exe"), StringComparison.OrdinalIgnoreCase)
            || string.Equals(executablePath, Path.Combine(windows, "SysWOW64", "conhost.exe"), StringComparison.OrdinalIgnoreCase);
    }
}
