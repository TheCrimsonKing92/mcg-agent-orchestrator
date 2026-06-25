using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ProjectCliCommand
{
    public static int Execute(
        IReadOnlyList<string> parts,
        OrchestratorProjectRegistry registry,
        string defaultRootDirectory,
        string? activeProjectOverride)
    {
        var subcommand = parts.Count > 1 ? parts[1].ToLowerInvariant() : "show";
        switch (subcommand)
        {
            case "list":
                PrintList(registry, defaultRootDirectory, activeProjectOverride);
                return 0;

            case "create":
                Create(parts, registry);
                return 0;

            case "select":
                Select(parts, registry);
                return 0;

            case "show":
                PrintShow(parts, registry, defaultRootDirectory, activeProjectOverride);
                return 0;

            default:
                throw new ArgumentException("Usage: project list|show [name]|create <name> --root <path>|select <name>");
        }
    }

    private static void PrintList(
        OrchestratorProjectRegistry registry,
        string defaultRootDirectory,
        string? activeProjectOverride)
    {
        var active = registry.ResolveActiveProject(defaultRootDirectory, activeProjectOverride);
        var defaultProject = new OrchestratorProject(OrchestratorWorkspace.DefaultProjectName, Path.GetFullPath(defaultRootDirectory));
        var projects = new[] { defaultProject }
            .Concat(registry.ListProjects())
            .OrderBy(project => project.Name.Equals(OrchestratorWorkspace.DefaultProjectName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(project => project.Name, StringComparer.OrdinalIgnoreCase);

        Console.WriteLine("Projects:");
        foreach (var project in projects)
        {
            var marker = project.Name.Equals(active.Name, StringComparison.OrdinalIgnoreCase) ? "*" : " ";
            var workspace = project.ResolveWorkspace();
            Console.WriteLine($"{marker} {project.Name}: root={project.RootDirectory} state={workspace.SqliteStatePath}");
        }
    }

    private static void Create(IReadOnlyList<string> parts, OrchestratorProjectRegistry registry)
    {
        if (parts.Count < 5)
        {
            throw new ArgumentException("Usage: project create <name> --root <path>");
        }

        var root = GetFlagValue(parts, "--root");
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("Usage: project create <name> --root <path>");
        }

        var project = registry.CreateProject(parts[2], root);
        var workspace = project.ResolveWorkspace();
        Directory.CreateDirectory(workspace.OrchestratorDirectory);
        new SqliteOrchestratorStateRepository(workspace.SqliteStatePath)
            .SaveAsync(new AgentOrchestratorKernel())
            .GetAwaiter()
            .GetResult();
        _ = new BacklogStore(workspace.BacklogStorePath);
        Console.WriteLine($"Project created: {project.Name}");
        Console.WriteLine($"Root: {project.RootDirectory}");
        Console.WriteLine($"Workspace: {workspace.OrchestratorDirectory}");
    }

    private static void Select(IReadOnlyList<string> parts, OrchestratorProjectRegistry registry)
    {
        if (parts.Count < 3)
        {
            throw new ArgumentException("Usage: project select <name>");
        }

        var name = OrchestratorProjectSelection.NormalizeProjectName(parts[2]);
        registry.SelectProject(name);
        Console.WriteLine($"Project selected: {name}");
    }

    private static void PrintShow(
        IReadOnlyList<string> parts,
        OrchestratorProjectRegistry registry,
        string defaultRootDirectory,
        string? activeProjectOverride)
    {
        OrchestratorProject project;
        if (parts.Count > 2)
        {
            var name = OrchestratorProjectSelection.NormalizeProjectName(parts[2]);
            project = name.Equals(OrchestratorWorkspace.DefaultProjectName, StringComparison.OrdinalIgnoreCase)
                ? new OrchestratorProject(OrchestratorWorkspace.DefaultProjectName, Path.GetFullPath(defaultRootDirectory))
                : registry.GetRequiredProject(name);
        }
        else
        {
            project = registry.ResolveActiveProject(defaultRootDirectory, activeProjectOverride);
        }

        var active = registry.ResolveActiveProject(defaultRootDirectory, activeProjectOverride);
        var workspace = project.ResolveWorkspace();
        Console.WriteLine($"Project: {project.Name}");
        Console.WriteLine($"Active: {project.Name.Equals(active.Name, StringComparison.OrdinalIgnoreCase)}");
        Console.WriteLine($"Root: {project.RootDirectory}");
        Console.WriteLine($"Workspace: {workspace.OrchestratorDirectory}");
        Console.WriteLine($"State: {workspace.SqliteStatePath}");
        Console.WriteLine($"Backlog: {workspace.BacklogStorePath}");
        Console.WriteLine($"Registry: {registry.RegistryPath}");
        Console.WriteLine($"Selection: {registry.CurrentProjectPath}");
    }

    private static string? GetFlagValue(IReadOnlyList<string> parts, string flag)
    {
        for (var index = 0; index < parts.Count; index++)
        {
            var part = parts[index];
            if (part.Equals(flag, StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= parts.Count)
                {
                    throw new ArgumentException($"Usage: {flag} <value>");
                }

                return parts[index + 1];
            }

            if (part.StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase))
            {
                return part[(flag.Length + 1)..];
            }
        }

        return null;
    }
}
