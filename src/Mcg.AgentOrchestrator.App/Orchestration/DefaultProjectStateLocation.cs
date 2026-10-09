using System.Security.Cryptography;
using System.Text;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// A relocation belongs to one repository, not to the implicit default project name.
public sealed record DefaultProjectStateLocation(
    string RepositoryRoot,
    string RelativeStateDirectory,
    string BackupDirectoryName,
    DateTimeOffset MovedAtUtc)
{
    internal static string CanonicalRoot(string root) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    internal static bool SameRoot(string left, string right) => string.Equals(
        CanonicalRoot(left), CanonicalRoot(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal static string DestinationSubpath(string root)
    {
        var canonical = CanonicalRoot(root);
        if (OperatingSystem.IsWindows()) canonical = canonical.ToLowerInvariant();
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16].ToLowerInvariant();
        return Path.Combine("default-project", key);
    }

    public string ResolveStateDirectory(string dataRoot)
    {
        if (string.IsNullOrWhiteSpace(RelativeStateDirectory) || Path.IsPathRooted(RelativeStateDirectory))
            throw new InvalidOperationException("Default state location must be relative to the data root.");
        var destination = Path.GetFullPath(Path.Combine(dataRoot, RelativeStateDirectory));
        if (!OrchestratorWorkspace.IsWithinDirectory(destination, dataRoot) || SameRoot(destination, dataRoot) ||
            OrchestratorWorkspace.IsWithinDirectory(destination, RepositoryRoot))
            throw new InvalidOperationException("Default state location must stay under the data root and outside the repository.");
        if (string.IsNullOrWhiteSpace(BackupDirectoryName) ||
            Path.GetFileName(BackupDirectoryName) != BackupDirectoryName ||
            !BackupDirectoryName.StartsWith(".orchestrator.backup-", StringComparison.Ordinal) ||
            BackupDirectoryName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException("Invalid default state backup directory name.");
        return destination;
    }
}
