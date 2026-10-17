namespace Mcg.AgentOrchestrator.Infrastructure;

// An image is a secondary identity check only after the loader resolves it.
internal static class ProcessImageResolution
{
    internal static bool IsResolved(string? imagePath) =>
        !string.IsNullOrWhiteSpace(imagePath) &&
        !string.Equals(Path.GetFileName(imagePath.Trim()), "ntdll.dll", StringComparison.OrdinalIgnoreCase);

    internal static bool SameProcess(SpawnProcessIdentity recorded, SpawnProcessIdentity observed) =>
        recorded.ProcessId == observed.ProcessId && recorded.StartedAt == observed.StartedAt &&
        (!IsResolved(recorded.ImagePath) || !IsResolved(observed.ImagePath) ||
            string.Equals(recorded.ImagePath, observed.ImagePath, StringComparison.OrdinalIgnoreCase));

    internal static string? PreferResolved(string? recorded, string? observed) =>
        IsResolved(observed) || !IsResolved(recorded) ? observed : recorded;
}
