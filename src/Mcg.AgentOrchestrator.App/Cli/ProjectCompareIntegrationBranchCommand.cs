using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ProjectCompareIntegrationBranchCommand
{
    private const string Usage = "Usage: project compare-integration-branch [name] [--root <path>]";

    internal static int Execute(IReadOnlyList<string> parts, OrchestratorProjectRegistry registry,
        string defaultRootDirectory, string? activeProjectOverride, TextWriter output)
    {
        var rootOverride = ParseOptions(parts);
        var name = parts.Count > 2 && !parts[2].StartsWith("--", StringComparison.Ordinal)
            ? OrchestratorProjectSelection.NormalizeProjectName(parts[2]) : null;
        var project = name is null ? registry.ResolveActiveProject(defaultRootDirectory, activeProjectOverride)
            : name.Equals(OrchestratorWorkspace.DefaultProjectName, StringComparison.OrdinalIgnoreCase)
                ? new OrchestratorProject(OrchestratorWorkspace.DefaultProjectName, Path.GetFullPath(defaultRootDirectory))
                : registry.GetRequiredProject(name);
        var learned = IntegrationBranchReader.Read(Path.GetFullPath(rootOverride ?? project.RootDirectory));
        var comparison = IntegrationBranchComparer.Compare(learned, project.IntegrationBranch);
        output.WriteLine($"{comparison.Kind} learned={comparison.LearnedBranch ?? "-"} registry={comparison.RegistryBranch} confidence={learned.Branch?.Confidence.ToString() ?? "-"} ({comparison.Reason})");
        return comparison.Kind == IntegrationBranchResultKind.Match ? 0 : 1;
    }

    private static string? ParseOptions(IReadOnlyList<string> parts)
    {
        string? root = null;
        var index = parts.Count > 2 && !parts[2].StartsWith("--", StringComparison.Ordinal) ? 3 : 2;
        for (; index < parts.Count; index++)
        {
            var option = parts[index];
            var equals = option.IndexOf('=');
            var flag = equals < 0 ? option : option[..equals];
            if (flag != "--root" || root is not null)
                throw new ArgumentException(Usage);
            var value = equals < 0 ? ++index < parts.Count ? parts[index] : null : option[(equals + 1)..];
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException(Usage);
            root = value;
        }

        return root;
    }
}
