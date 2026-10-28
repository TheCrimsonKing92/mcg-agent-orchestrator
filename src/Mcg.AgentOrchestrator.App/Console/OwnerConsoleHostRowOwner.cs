using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Immutable ownership evidence, independent of the workspace being displayed.
internal sealed class OwnerConsoleHostRowOwner
{
    internal const string Home = "home";
    internal const string Host = "host";
    internal const string Unknown = "unknown";
    private readonly (string Owner, string Directory)[] _directories;

    internal OwnerConsoleHostRowOwner(string? homeDirectory,
        IEnumerable<(string Owner, string Directory)> projects)
    {
        _directories = projects.Concat(homeDirectory is null ? [] : [(Home, homeDirectory)])
            .Select(entry => (entry.Owner, Directory: Normalize(entry.Directory)))
            .Where(entry => entry.Directory is not null)
            .Select(entry => (entry.Owner, entry.Directory!)).ToArray();
    }

    internal static OwnerConsoleHostRowOwner FromRegistry(string repositoryRoot, OrchestratorProjectRegistry registry)
    {
        string? home = null;
        (string Owner, string Directory)[] projects = [];
        try { home = OrchestratorWorkspace.ForDirectory(repositoryRoot, null, null, registry).OrchestratorDirectory; }
        catch (Exception ex) when (IsUnreadable(ex)) { }
        try
        {
            var dataRoot = OrchestratorDataRoot.FromDirectory(registry.DataRootDirectory);
            projects = registry.ListProjects().Select(project => (project.Name, dataRoot.ProjectDirectory(project.Name))).ToArray();
        }
        catch (Exception ex) when (IsUnreadable(ex)) { }
        return new(home, projects);
    }

    internal string ForPath(string? path)
    {
        var normalized = Normalize(path);
        if (normalized is null) return Unknown;
        var matches = _directories.Where(entry => OrchestratorWorkspace.IsWithinDirectory(normalized, entry.Directory)).ToArray();
        if (matches.Length == 0) return Unknown;
        var longest = matches.Max(entry => entry.Directory.Length);
        var owners = matches.Where(entry => entry.Directory.Length == longest).Select(entry => entry.Owner)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return owners.Length == 1 ? owners[0] : Unknown;
    }

    internal string ForRunningLanes(IEnumerable<OwnerConsoleGateLane> lanes)
    {
        var owners = lanes.Select(lane => ForPath(lane.HeartbeatPath)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return owners.Length == 0 ? Host : owners.Length == 1 ? owners[0] : Unknown;
    }

    // Attempt ids contain a goal prefix, timestamp and guid, with no project mapping.
    internal string ForAttempts(IEnumerable<string> attemptIds) => attemptIds.Any() ? Unknown : Host;

    private static string? Normalize(string? path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ? null :
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (IsUnreadable(ex)) { return null; }
    }

    private static bool IsUnreadable(Exception ex) => ex is IOException or UnauthorizedAccessException or
        ArgumentException or NotSupportedException or JsonException or InvalidOperationException or System.Security.SecurityException;
}
