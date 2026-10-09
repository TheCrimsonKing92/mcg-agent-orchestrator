using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.Cli;

/// <summary>Keeps foreign, unregistered discovery snapshots in a project workspace.</summary>
internal static class ProjectDiscoveryModelLocation
{
    internal static string ResolveDirectory(
        OrchestratorProject project, string? rootOverride, string defaultRootDirectory, string dataRootDirectory)
    {
        var workspace = project.ResolveWorkspace();
        if (workspace.IsProjectScoped || rootOverride is null)
            return workspace.OrchestratorDirectory;

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootOverride));
        var ownRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(defaultRootDirectory));
        if (root.Equals(ownRoot, StringComparison.OrdinalIgnoreCase))
            return workspace.OrchestratorDirectory;

        var name = Path.GetFileName(root);
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Discovery root must have a directory name usable as a separate project workspace.", nameof(rootOverride));

        // A repository directory is a filesystem identity, not a registered project name.
        // Reuse the registered data root and projects folder, preserving the leaf verbatim.
        return Path.Combine(OrchestratorDataRoot.FromDirectory(dataRootDirectory).ProjectsDirectory, name);
    }
}
