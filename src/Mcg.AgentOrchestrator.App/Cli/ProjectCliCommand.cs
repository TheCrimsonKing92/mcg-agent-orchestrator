using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.RegularExpressions;

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
                Create(parts, registry, defaultRootDirectory);
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

    private static void Create(
        IReadOnlyList<string> parts,
        OrchestratorProjectRegistry registry,
        string defaultRootDirectory)
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

        var alreadyRegistered = false;
        var project = RunCreateStep(
            "project validation",
            () => registry.ResolveProjectForCreation(parts[2], root, out alreadyRegistered));
        if (alreadyRegistered)
        {
            Console.WriteLine($"Project already exists: {project.Name}");
            Console.WriteLine($"Root: {project.RootDirectory}");
            Console.WriteLine($"Workspace: {project.ResolveWorkspace().OrchestratorDirectory}");
            return;
        }

        var sourceWorkspace = OrchestratorWorkspace.ForDirectory(defaultRootDirectory);
        var snapshot = RunCreateStep(
            "source configuration validation",
            () => LoadConfigurationSnapshot(sourceWorkspace, project.ResolveWorkspace()));
        var workspace = project.ResolveWorkspace();
        EnsureDestinationAvailable(workspace.OrchestratorDirectory);

        var parentDirectory = Path.GetDirectoryName(workspace.OrchestratorDirectory)
            ?? throw new InvalidOperationException("Project workspace has no parent directory.");
        var stagingDirectory = Path.Combine(
            parentDirectory,
            $".{project.Name}.bootstrap-{Guid.NewGuid():N}");

        try
        {
            RunCreateStep("configuration staging", () =>
            {
                Directory.CreateDirectory(stagingDirectory);
                ModelFunctionCatalogStore.Save(
                    Path.Combine(stagingDirectory, Path.GetFileName(workspace.ModelFunctionCatalogPath)),
                    snapshot.ModelFunctions);
                AgentCatalogStore.Save(
                    Path.Combine(stagingDirectory, Path.GetFileName(workspace.AgentCatalogPath)),
                    snapshot.Agents);
                WorkerProfileStore.Save(
                    Path.Combine(stagingDirectory, Path.GetFileName(workspace.WorkerProfilePath)),
                    snapshot.WorkerProfiles);
            });
            RunCreateStep("runtime store initialization", () =>
            {
                var statePath = Path.Combine(stagingDirectory, Path.GetFileName(workspace.SqliteStatePath));
                _ = StateDbMigrations.EnsureUpToDate(statePath);
                new SqliteOrchestratorStateRepository(statePath)
                    .SaveAsync(new AgentOrchestratorKernel())
                    .GetAwaiter()
                    .GetResult();
                _ = new BacklogStore(
                    Path.Combine(stagingDirectory, Path.GetFileName(workspace.BacklogStorePath)));
            });
            RunCreateStep("project workspace commit", () =>
            {
                if (Directory.Exists(workspace.OrchestratorDirectory))
                {
                    Directory.Delete(workspace.OrchestratorDirectory);
                }

                Directory.Move(stagingDirectory, workspace.OrchestratorDirectory);
            });
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                try
                {
                    Directory.Delete(stagingDirectory, recursive: true);
                }
                catch
                {
                    // Preserve the original step-specific exception. The unregistered staging
                    // directory is recoverable and includes its bootstrap marker in the name.
                }
            }
        }

        RunCreateStep("registry registration", () => registry.CreateProject(project.Name, project.RootDirectory));
        Console.WriteLine($"Project created: {project.Name}");
        Console.WriteLine($"Root: {project.RootDirectory}");
        Console.WriteLine($"Workspace: {workspace.OrchestratorDirectory}");
    }

    private static ProjectConfigurationSnapshot LoadConfigurationSnapshot(
        OrchestratorWorkspace source,
        OrchestratorWorkspace destination)
    {
        EnsureReadableIfPresent(source.ModelFunctionCatalogPath);
        EnsureReadableIfPresent(source.AgentCatalogPath);
        EnsureReadableIfPresent(source.WorkerProfilePath);

        var modelFunctions = ModelFunctionCatalogStore.Load(source.ModelFunctionCatalogPath);
        var matchingRefiners = modelFunctions.Bindings
            .Where(binding => string.Equals(
                string.IsNullOrWhiteSpace(binding.Name) ? binding.Purpose : binding.Name,
                ModelFunctionPurposes.SpecRefiner,
                StringComparison.Ordinal))
            .ToList();
        if (matchingRefiners.Count != 1)
        {
            var detail = matchingRefiners.Count == 0 ? "missing" : "ambiguous";
            throw new InvalidOperationException(
                $"Source model-function catalog has a {detail} '{ModelFunctionPurposes.SpecRefiner}' binding. " +
                "Configure the invoking default project before creating an isolated project.");
        }

        var agents = AgentCatalogStore.Load(source.AgentCatalogPath);
        if (agents.Agents.Count == 0)
        {
            throw new InvalidOperationException("Source agent catalog did not produce any agents.");
        }

        var workerProfiles = WorkerProfileStore.Load(source.WorkerProfilePath);
        if (workerProfiles.Profiles.Count == 0)
        {
            throw new InvalidOperationException("Source worker-profile catalog did not produce any profiles.");
        }

        var refiner = matchingRefiners[0];
        if (string.IsNullOrWhiteSpace(refiner.Model.ProviderName) ||
            string.IsNullOrWhiteSpace(refiner.Model.ModelName))
        {
            throw new InvalidOperationException(
                $"Source '{ModelFunctionPurposes.SpecRefiner}' binding must name a provider and model.");
        }

        if (refiner.Subscription is { } refinerSubscription &&
            !workerProfiles.Profiles.Any(profile =>
                profile.Name.Equals(refinerSubscription.WorkerProfileName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Source '{ModelFunctionPurposes.SpecRefiner}' binding references missing worker profile " +
                $"'{refinerSubscription.WorkerProfileName}'.");
        }

        var missingAgentWorkerProfile = agents.Agents
            .Where(agent => agent.Subscription is not null)
            .Select(agent => agent.Subscription!.WorkerProfileName)
            .FirstOrDefault(profileName => !workerProfiles.Profiles.Any(profile =>
                profile.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase)));
        if (missingAgentWorkerProfile is not null)
        {
            throw new InvalidOperationException(
                $"Source agent catalog references missing worker profile '{missingAgentWorkerProfile}'.");
        }

        return new ProjectConfigurationSnapshot(
            RewriteProjectPaths(modelFunctions, source.RootDirectory, destination.RootDirectory),
            RewriteProjectPaths(agents, source.RootDirectory, destination.RootDirectory),
            RewriteProjectPaths(workerProfiles, source.RootDirectory, destination.RootDirectory));
    }

    private static ModelFunctionCatalog RewriteProjectPaths(
        ModelFunctionCatalog catalog,
        string sourceRoot,
        string destinationRoot) =>
        new(catalog.Bindings
            .Select(binding => binding with
            {
                Model = RewriteProjectPaths(binding.Model, sourceRoot, destinationRoot)
            })
            .ToList());

    private static AgentCatalog RewriteProjectPaths(
        AgentCatalog catalog,
        string sourceRoot,
        string destinationRoot) =>
        new(catalog.Agents
            .Select(agent => agent with
            {
                Model = RewriteProjectPaths(agent.Model, sourceRoot, destinationRoot),
                ComplexModel = agent.ComplexModel is null
                    ? null
                    : RewriteProjectPaths(agent.ComplexModel, sourceRoot, destinationRoot)
            })
            .ToList());

    private static WorkerProfileCatalog RewriteProjectPaths(
        WorkerProfileCatalog catalog,
        string sourceRoot,
        string destinationRoot) =>
        new(catalog.Profiles
            .Select(profile => profile with
            {
                CommandTemplate = RewriteProjectPathText(
                    profile.CommandTemplate,
                    sourceRoot,
                    destinationRoot)
            })
            .ToList());

    private static ModelProfile RewriteProjectPaths(
        ModelProfile model,
        string sourceRoot,
        string destinationRoot) =>
        model with
        {
            ModelName = RewriteProjectPathValue(model.ModelName, sourceRoot, destinationRoot)
        };

    private static string RewriteProjectPathValue(
        string value,
        string sourceRoot,
        string destinationRoot)
    {
        if (!Path.IsPathFullyQualified(value))
        {
            return value;
        }

        var fullValue = Path.GetFullPath(value);
        var normalizedSource = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        if (!fullValue.Equals(normalizedSource, PathComparison) &&
            !fullValue.StartsWith(normalizedSource + Path.DirectorySeparatorChar, PathComparison))
        {
            return value;
        }

        return Path.Combine(
            Path.GetFullPath(destinationRoot),
            Path.GetRelativePath(normalizedSource, fullValue));
    }

    private static string RewriteProjectPathText(
        string value,
        string sourceRoot,
        string destinationRoot)
    {
        var normalizedSource = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
        var normalizedDestination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationRoot));
        var rewritten = ReplacePathRoot(value, normalizedSource, normalizedDestination);
        var alternateSource = normalizedSource.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var alternateDestination = normalizedDestination.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return alternateSource.Equals(normalizedSource, StringComparison.Ordinal)
            ? rewritten
            : ReplacePathRoot(rewritten, alternateSource, alternateDestination);
    }

    private static string ReplacePathRoot(string value, string sourceRoot, string destinationRoot)
    {
        var pattern = Regex.Escape(sourceRoot) + "(?=$|[\\\\/\"' \\t\\r\\n])";
        var options = OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None;
        return Regex.Replace(value, pattern, _ => destinationRoot, options);
    }

    private static void EnsureReadableIfPresent(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        _ = stream.Length;
    }

    private static void EnsureDestinationAvailable(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"Project creation failed during destination validation: workspace path is a link or junction: {path}");
        }

        if (Directory.EnumerateFileSystemEntries(path).Any())
        {
            throw new InvalidOperationException(
                $"Project creation failed during destination validation: workspace directory is not empty: {path}");
        }
    }

    private static T RunCreateStep<T>(string step, Func<T> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Project creation failed during {step}: {ex.Message}",
                ex);
        }
    }

    private static void RunCreateStep(string step, Action action) =>
        RunCreateStep(step, () =>
        {
            action();
            return true;
        });

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed record ProjectConfigurationSnapshot(
        ModelFunctionCatalog ModelFunctions,
        AgentCatalog Agents,
        WorkerProfileCatalog WorkerProfiles);

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
