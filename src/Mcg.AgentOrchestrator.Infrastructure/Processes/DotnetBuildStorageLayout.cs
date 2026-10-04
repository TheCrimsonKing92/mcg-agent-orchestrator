using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class DotnetBuildStorageLayout
{
    public const string RootDirectoryName = "mcg-dotnet-isolated";

    // Supported escape hatch for tests that need lease-root isolation.
    public const string IsolatedRootOverrideVariable = "MCG_DOTNET_ISOLATED_ROOT";

    internal const string LeaseDirectoryName = "lease";

    internal const string LeaseMetadataFileName = "lease.json";

    internal const string LandingTestsRootDirectoryName = "mcg-landing-tests";

    public static string GoalRoot(GoalId goalId, DotnetBuildStorageRoot? storageRoot = null)
    {
        return Path.Combine((storageRoot ?? CaptureStorageRoot()).RootPath, "goals", Prefix(goalId));
    }

    public static string GoalArtifactsPath(GoalId goalId, DotnetBuildStorageRoot? storageRoot = null)
    {
        storageRoot ??= CaptureStorageRoot();
        return TryReadArtifactsPath(Path.Combine(LeaseDirectory(goalId, storageRoot), LeaseMetadataFileName)) ??
            Path.Combine(GoalRoot(goalId, storageRoot), "artifacts");
    }

    internal static string LeaseDirectory(GoalId goalId, DotnetBuildStorageRoot storageRoot)
    {
        return Path.Combine(GoalRoot(goalId, storageRoot), LeaseDirectoryName);
    }

    public static DotnetBuildStorageRoot CaptureStorageRoot()
    {
        return new DotnetBuildStorageRoot(Path.GetFullPath(ResolveIsolatedRootBase(
            Environment.GetEnvironmentVariable(IsolatedRootOverrideVariable),
            Environment.GetEnvironmentVariable("LOCALAPPDATA"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.GetTempPath(),
            OperatingSystem.IsWindows())));
    }

    internal static string ResolveIsolatedRootBase(
        string? overridden,
        string? localAppDataVariable,
        string? localAppDataKnownFolder,
        string tempPath,
        bool isWindows)
    {
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return overridden;
        }

        if (isWindows)
        {
            // Nested hermetic acceptance processes redirect USERPROFILE, so GetFolderPath can resolve
            // beneath mcg-hvp even though the parent explicitly preserved the real LOCALAPPDATA. Prefer
            // that inherited value so C# callers share the same machine-user Low-integrity slot grid as
            // Invoke-IsolatedDotnet.ps1 and Invoke-WorkerBuildCheck.ps1.
            var localAppData = string.IsNullOrWhiteSpace(localAppDataVariable)
                ? localAppDataKnownFolder
                : localAppDataVariable;
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                return Path.GetFullPath(Path.Combine(localAppData, "..", "LocalLow", RootDirectoryName));
            }
        }

        return Path.Combine(tempPath, RootDirectoryName);
    }

    internal static string? TryReadArtifactsPath(string metadataPath)
    {
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            return document.RootElement.TryGetProperty("artifactsPath", out var artifactsPath) &&
                artifactsPath.ValueKind == JsonValueKind.String
                    ? artifactsPath.GetString()
                    : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal static string Prefix(GoalId goalId)
    {
        var value = goalId.Value;
        return (value.Length <= 8 ? value : value[..8]).ToLowerInvariant();
    }
}
