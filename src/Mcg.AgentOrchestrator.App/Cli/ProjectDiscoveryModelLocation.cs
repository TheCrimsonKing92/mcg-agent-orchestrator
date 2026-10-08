using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

/// <summary>Keeps foreign, unregistered discovery snapshots in a project workspace.</summary>
internal static class ProjectDiscoveryModelLocation
{
    internal static string ResolveDirectory(
        OrchestratorProject project, string? rootOverride, string defaultRootDirectory)
    {
        var workspace = project.ResolveWorkspace();
        if (workspace.IsProjectScoped || rootOverride is null)
            return workspace.OrchestratorDirectory;

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootOverride));
        var ownRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(defaultRootDirectory));
        if (root.Equals(ownRoot, StringComparison.OrdinalIgnoreCase))
            return workspace.OrchestratorDirectory;

        var name = Path.GetFileName(root);
        if (string.IsNullOrEmpty(name) || name.Equals(OrchestratorWorkspace.DefaultProjectName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Discovery root must have a directory name usable as a separate project workspace.", nameof(rootOverride));

        return OrchestratorWorkspace.ForProject(name, defaultRootDirectory).OrchestratorDirectory;
    }
}
