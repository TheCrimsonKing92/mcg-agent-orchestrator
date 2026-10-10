using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ProjectCompareManifestCommand
{
    internal const string Usage = "Usage: project compare-manifest [name] [--root <path>] --manifest <path>";

    internal static int Execute(IReadOnlyList<string> parts, OrchestratorProjectRegistry registry,
        string defaultRootDirectory, string? activeProjectOverride, TextWriter output)
    {
        var (rootOverride, manifestPath) = ParseOptions(parts);
        string manifestJson;
        try
        {
            manifestJson = File.ReadAllText(Path.GetFullPath(manifestPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw ManifestError(exception);
        }

        var name = parts.Count > 2 && !parts[2].StartsWith("--", StringComparison.Ordinal)
            ? OrchestratorProjectSelection.NormalizeProjectName(parts[2]) : null;
        var project = name is null ? registry.ResolveActiveProject(defaultRootDirectory, activeProjectOverride)
            : name.Equals(OrchestratorWorkspace.DefaultProjectName, StringComparison.OrdinalIgnoreCase)
                ? new OrchestratorProject(OrchestratorWorkspace.DefaultProjectName, Path.GetFullPath(defaultRootDirectory))
                : registry.GetRequiredProject(name);
        var model = new DotnetProjectDiscoveryAdapter().Discover(rootOverride ?? project.RootDirectory,
            RepositorySourceInventory.ExcludedDirectoryNames, null, UnitCommandKinds.None);
        IReadOnlyList<ManifestCheckComparison> comparisons;
        try
        {
            comparisons = ManifestCheckComparer.Compare(model, manifestJson);
        }
        catch (JsonException exception)
        {
            throw ManifestError(exception);
        }

        foreach (var comparison in comparisons)
        {
            var reason = string.IsNullOrEmpty(comparison.Reason) ? "" : $" ({comparison.Reason})";
            output.WriteLine($"{comparison.Kind} {comparison.ProjectPath} learned={comparison.LearnedRunner ?? "-"} manifest={comparison.ManifestRunner ?? "-"}{reason}");
        }

        return comparisons.All(comparison => comparison.Kind == ManifestCheckResultKind.Match) ? 0 : 1;
    }

    private static (string? Root, string Manifest) ParseOptions(IReadOnlyList<string> parts)
    {
        string? root = null;
        string? manifest = null;
        var index = parts.Count > 2 && !parts[2].StartsWith("--", StringComparison.Ordinal) ? 3 : 2;
        for (; index < parts.Count; index++)
        {
            var option = parts[index];
            var equals = option.IndexOf('=');
            var flag = equals < 0 ? option : option[..equals];
            if (flag is not "--root" and not "--manifest")
                throw new ArgumentException(Usage);
            var value = equals < 0 ? ++index < parts.Count ? parts[index] : null : option[(equals + 1)..];
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException(Usage);
            if (flag == "--root" && root is null)
                root = value;
            else if (flag == "--manifest" && manifest is null)
                manifest = value;
            else
                throw new ArgumentException(Usage);
        }

        return (root, manifest ?? throw new ArgumentException(Usage));
    }

    private static ArgumentException ManifestError(Exception exception) =>
        new($"{Usage}{Environment.NewLine}Manifest could not be read: {exception.Message}", exception);
}
