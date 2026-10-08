using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ProjectCliCommand
{
    private const string DiscoverUsage = "Usage: project discover [name] [--root <path>] [--measure build|test|all]";
    public static int Execute(
        IReadOnlyList<string> parts,
        OrchestratorProjectRegistry registry,
        string defaultRootDirectory,
        string? activeProjectOverride,
        TextWriter? discoveryOutput = null,
        Func<IUnitCommandMeasurer>? measurerFactory = null)
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

            case "discover":
                Discover(parts, registry, defaultRootDirectory, activeProjectOverride, discoveryOutput ?? Console.Out, measurerFactory);
                return 0;

            default:
                throw new ArgumentException("Usage: project list|show [name]|discover [name] [--root <path>] [--measure build|test|all]|create <name> --root <path> [--integration-branch <name>] [--relocate-state]|select <name>");
        }
    }

    private static void Discover(
        IReadOnlyList<string> parts,
        OrchestratorProjectRegistry registry,
        string defaultRootDirectory,
        string? activeProjectOverride,
        TextWriter output,
        Func<IUnitCommandMeasurer>? measurerFactory)
    {
        var (rootOverride, measuredKinds) = ParseDiscoveryOptions(parts);

        var name = parts.Count > 2 && !parts[2].StartsWith("--", StringComparison.Ordinal)
            ? OrchestratorProjectSelection.NormalizeProjectName(parts[2]) : null;
        var project = name is null ? registry.ResolveActiveProject(defaultRootDirectory, activeProjectOverride)
            : name.Equals(OrchestratorWorkspace.DefaultProjectName, StringComparison.OrdinalIgnoreCase)
                ? new OrchestratorProject(OrchestratorWorkspace.DefaultProjectName, Path.GetFullPath(defaultRootDirectory))
                : registry.GetRequiredProject(name);
        IProjectDiscoveryAdapter adapter = new DotnetProjectDiscoveryAdapter();
        var measurer = measuredKinds is null ? null : measurerFactory is null ? new ProcessUnitCommandMeasurer()
            : measurerFactory() ?? throw new InvalidOperationException("The measurement factory returned no measurer.");
        var model = adapter.Discover(rootOverride ?? project.RootDirectory, RepositorySourceInventory.ExcludedDirectoryNames,
            measurer, measuredKinds ?? UnitCommandKinds.None);
        var workspaceDirectory = ProjectDiscoveryModelLocation.ResolveDirectory(project, rootOverride, defaultRootDirectory);
        Directory.CreateDirectory(workspaceDirectory);
        var modelPath = Path.Combine(workspaceDirectory, "project-model.json");
        var temporaryPath = Path.Combine(workspaceDirectory, $".project-model-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, ProjectModelJson.Serialize(model));
            File.Move(temporaryPath, modelPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }

        output.WriteLine($"Project model: {modelPath}");
        foreach (var question in model.OwnerQuestions)
            output.WriteLine($"Owner question: {question.Question}");
    }

    private static (string? Root, UnitCommandKinds? Kinds) ParseDiscoveryOptions(IReadOnlyList<string> parts)
    {
        string? root = null;
        UnitCommandKinds? kinds = null;
        var index = parts.Count > 2 && !parts[2].StartsWith("--", StringComparison.Ordinal) ? 3 : 2;
        for (; index < parts.Count; index++)
        {
            var option = parts[index];
            var equals = option.IndexOf('=');
            var flag = equals < 0 ? option : option[..equals];
            if (!flag.Equals("--root", StringComparison.OrdinalIgnoreCase) && !flag.Equals("--measure", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(DiscoverUsage);
            var value = equals >= 0 ? option[(equals + 1)..] : ++index < parts.Count ? parts[index] : null;
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException(DiscoverUsage);
            if (flag.Equals("--root", StringComparison.OrdinalIgnoreCase))
            {
                if (root is not null) throw new ArgumentException(DiscoverUsage);
                root = value;
            }
            else
            {
                if (kinds is not null) throw new ArgumentException(DiscoverUsage);
                kinds = value.ToLowerInvariant() switch
                {
                    "build" => UnitCommandKinds.Build,
                    "test" => UnitCommandKinds.Test,
                    "all" => UnitCommandKinds.All,
                    _ => throw new ArgumentException(DiscoverUsage)
                };
            }
        }
        return (root, kinds);
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
            throw new ArgumentException("Usage: project create <name> --root <path> [--integration-branch <name>] [--relocate-state]");
        }

        var root = GetFlagValue(parts, "--root");
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("Usage: project create <name> --root <path> [--integration-branch <name>] [--relocate-state]");
        }

        var integrationBranch = GetFlagValue(parts, "--integration-branch");
        if (integrationBranch is not null)
        {
            try
            {
                TrunkBranchName.Validate(integrationBranch);
            }
            catch (ArgumentException)
            {
                throw new ArgumentException("Usage: project create <name> --root <path> [--integration-branch <name>] [--relocate-state]");
            }
        }

        var relocateState = parts.Any(part => part.Equals("--relocate-state", StringComparison.OrdinalIgnoreCase));
        var alreadyRegistered = false;
        var project = RunCreateStep(
            "project validation",
            () => registry.ResolveProjectForCreation(parts[2], root, integrationBranch, out alreadyRegistered));
        var workspace = RunCreateStep("destination validation", () => project.ResolveWorkspace());
        var legacyDirectory = OrchestratorWorkspace.LegacyProjectDirectory(project.RootDirectory, project.Name);
        if (relocateState && !alreadyRegistered)
            throw new InvalidOperationException("Project creation failed during relocation validation: --relocate-state requires an existing registration.");
        if (Directory.Exists(legacyDirectory))
        {
            if (!alreadyRegistered || !relocateState)
                throw new InvalidOperationException(
                    "Project creation failed during relocation validation: legacy workspace is inside the target; use --relocate-state on its existing registration.");
            RunCreateStep("state relocation", () => RelocateLegacyState(legacyDirectory, workspace.OrchestratorDirectory, project.Name));
        }
        if (alreadyRegistered)
        {
            Console.WriteLine($"Project already exists: {project.Name}");
            Console.WriteLine($"Root: {project.RootDirectory}");
            Console.WriteLine($"Workspace: {workspace.OrchestratorDirectory}");
            Console.WriteLine($"State: {workspace.SqliteStatePath}");
            return;
        }

        var sourceWorkspace = OrchestratorWorkspace.ForDirectory(defaultRootDirectory);
        var snapshot = RunCreateStep(
            "source configuration validation",
            () => LoadConfigurationSnapshot(sourceWorkspace, workspace));
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

        RunCreateStep("registry registration", () => registry.CreateProject(project.Name, project.RootDirectory, integrationBranch));
        Console.WriteLine($"Project created: {project.Name}");
        Console.WriteLine($"Root: {project.RootDirectory}");
        Console.WriteLine($"Workspace: {workspace.OrchestratorDirectory}");
        Console.WriteLine($"State: {workspace.SqliteStatePath}");
    }

    private static void RelocateLegacyState(string source, string destination, string projectName)
    {
        // Do not follow junctions out of the project tree or remove unrelated target files.
        var directories = new List<string> { source };
        var files = new List<string>();
        for (var index = 0; index < directories.Count; index++)
        {
            var directory = directories[index];
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"Legacy workspace contains a link or junction: {directory}");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException($"Legacy workspace contains a link or junction: {entry}");
                if ((attributes & FileAttributes.Directory) != 0)
                    directories.Add(entry);
                else
                    files.Add(entry);
            }
        }

        EnsureDestinationAvailable(destination);
        var parent = Path.GetDirectoryName(destination)!;
        var staging = Path.Combine(parent, $".{projectName}.relocate-{Guid.NewGuid():N}");
        try
        {
            // Copy first so source and destination may be on different volumes.
            Directory.CreateDirectory(staging);
            foreach (var directory in directories.Skip(1))
                Directory.CreateDirectory(Path.Combine(staging, Path.GetRelativePath(source, directory)));
            foreach (var file in files)
            {
                var copy = Path.Combine(staging, Path.GetRelativePath(source, file));
                File.Copy(file, copy);
                using var originalStream = File.OpenRead(file);
                using var copyStream = File.OpenRead(copy);
                if (!SHA256.HashData(originalStream).AsSpan().SequenceEqual(SHA256.HashData(copyStream)))
                    throw new IOException($"Relocation copy verification failed: {file}");
            }
            foreach (var file in files.Where(path => Path.GetFileName(path).Equals("state.db", StringComparison.OrdinalIgnoreCase)))
            {
                var database = Path.Combine(staging, Path.GetRelativePath(source, file));
                // Probe the detached copy: WAL readers can create sidecars even in read-only mode.
                // Unknown schemas fail closed, without migrating or touching the legacy store.
                using (var connection = StateDbConnectionFactory.Open(database, StateDbConnectionProfile.FastFailRead))
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "SELECT EXISTS(SELECT 1 FROM goals)";
                    if (Convert.ToInt64(command.ExecuteScalar()) != 0)
                        throw new InvalidOperationException($"Legacy state database contains goals; relocation refused: {file}");
                }
                foreach (var suffix in new[] { "-wal", "-shm" })
                {
                    if (!files.Contains(file + suffix) && File.Exists(database + suffix))
                        File.Delete(database + suffix);
                }
            }
            // Refuse a source that changed during validation rather than deleting newer bytes.
            var currentFiles = Directory.GetFiles(source, "*", SearchOption.AllDirectories);
            if (!currentFiles.Order(StringComparer.Ordinal).SequenceEqual(files.Order(StringComparer.Ordinal)))
                throw new IOException("Legacy workspace changed during relocation; source retained.");
            foreach (var file in files)
            {
                using var originalStream = File.OpenRead(file);
                using var copyStream = File.OpenRead(Path.Combine(staging, Path.GetRelativePath(source, file)));
                if (!SHA256.HashData(originalStream).AsSpan().SequenceEqual(SHA256.HashData(copyStream)))
                    throw new IOException($"Legacy workspace changed during relocation; source retained: {file}");
            }
            if (Directory.Exists(destination))
                Directory.Delete(destination); // Only the empty destination validated above.
            Directory.Move(staging, destination);
            Directory.Delete(source, recursive: true);
            var projectsDirectory = Path.GetDirectoryName(source)!;
            RemoveDirectoryIfEmpty(projectsDirectory);
            RemoveDirectoryIfEmpty(Path.GetDirectoryName(projectsDirectory)!);
        }
        finally
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    private static void RemoveDirectoryIfEmpty(string directory)
    {
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            Directory.Delete(directory);
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
        Console.WriteLine($"Integration branch: {project.IntegrationBranch}");
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
