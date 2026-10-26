namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class ProcessStopRevalidation
{
    internal static string? Evaluate(
        ProcessInspectionRecord recorded,
        ProcessInspectionRecord? current,
        IReadOnlyList<string> commandMarkers)
    {
        if (recorded.StartedAt is null || string.IsNullOrWhiteSpace(recorded.ExecutablePath))
        {
            return "identity-unavailable";
        }

        if (current is null ||
            current.Status != ProcessInspectionStatus.Available ||
            string.IsNullOrWhiteSpace(current.CommandLine))
        {
            return "inspection-unavailable";
        }

        var recordedPath = NormalizeIdentityPath(recorded.ExecutablePath);
        var currentPath = NormalizeIdentityPath(current.ExecutablePath);
        if (current.StartedAt != recorded.StartedAt ||
            recordedPath is null ||
            currentPath is null ||
            !string.Equals(currentPath, recordedPath, StringComparison.OrdinalIgnoreCase))
        {
            return "identity-mismatch";
        }

        return commandMarkers.Any(marker =>
            current.CommandLine.IndexOf(marker, StringComparison.OrdinalIgnoreCase) < 0)
                ? "command-changed"
                : null;
    }

    private static string? NormalizeIdentityPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try { return Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return null; }
    }
}
