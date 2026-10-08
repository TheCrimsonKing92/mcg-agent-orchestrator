using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

public sealed class OrchestratorProjectRegistry
{
    public const string RegistryHomeEnvironmentVariable = "MCG_ORCHESTRATOR_PROJECT_REGISTRY";
    private const string RegistryFileName = "projects.json";
    private const string CurrentProjectFileName = "current-project.txt";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public OrchestratorProjectRegistry(string registryDirectory, string? dataRootDirectory = null)
    {
        RegistryDirectory = Path.GetFullPath(registryDirectory);
        DataRootDirectory = dataRootDirectory is null
            ? OrchestratorDataRoot.Resolve().RootDirectory
            : Path.GetFullPath(dataRootDirectory);
    }

    public string RegistryDirectory { get; }
    public string DataRootDirectory { get; }
    public string RegistryPath => Path.Combine(RegistryDirectory, RegistryFileName);
    public string CurrentProjectPath => Path.Combine(RegistryDirectory, CurrentProjectFileName);

    public static OrchestratorProjectRegistry CreateDefault(Func<string, string?>? readEnvironment = null)
    {
        var configured = (readEnvironment ?? Environment.GetEnvironmentVariable)(RegistryHomeEnvironmentVariable);
        var dataRoot = OrchestratorDataRoot.Resolve(readEnvironment).RootDirectory;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return new OrchestratorProjectRegistry(configured, dataRoot);
        }

        return new OrchestratorProjectRegistry(
            Path.Combine(OrchestratorDataRoot.ResolveDefaultDirectory(), "projects"), dataRoot);
    }

    public IReadOnlyList<OrchestratorProject> ListProjects()
    {
        var file = LoadFile();
        return file.Projects
            .Select(entry => new OrchestratorProject(entry.Name, Path.GetFullPath(entry.RootDirectory))
            {
                IntegrationBranch = ResolveStoredBranch(entry),
                DataRootDirectory = DataRootDirectory
            })
            .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public OrchestratorProject CreateProject(string name, string rootDirectory, string? integrationBranch = null)
    {
        var project = ResolveProjectForCreation(name, rootDirectory, integrationBranch, out var alreadyRegistered);
        if (alreadyRegistered)
        {
            return project;
        }

        var file = LoadFile();
        var entry = new ProjectEntry(project.Name, project.RootDirectory) { IntegrationBranch = integrationBranch };
        file.Projects.Add(entry);
        SaveFile(file);
        return project;
    }

    public OrchestratorProject ResolveProjectForCreation(
        string name,
        string rootDirectory,
        out bool alreadyRegistered) =>
        ResolveProjectForCreation(name, rootDirectory, null, out alreadyRegistered);

    public OrchestratorProject ResolveProjectForCreation(
        string name,
        string rootDirectory,
        string? integrationBranch,
        out bool alreadyRegistered)
    {
        var branch = integrationBranch is null ? TrunkBranchName.Default : TrunkBranchName.Validate(integrationBranch);
        var normalizedName = OrchestratorProjectSelection.NormalizeProjectName(name);
        if (normalizedName.Equals(OrchestratorWorkspace.DefaultProjectName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The default project is implicit and cannot be created.");
        }

        var root = Path.GetFullPath(rootDirectory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Project root does not exist: {root}");
        }

        var existing = LoadFile().Projects.FirstOrDefault(project =>
            project.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            alreadyRegistered = false;
            return new OrchestratorProject(normalizedName, root)
                { IntegrationBranch = branch, DataRootDirectory = DataRootDirectory };
        }

        var existingRoot = Path.GetFullPath(existing.RootDirectory);
        if (!PathsEqual(existingRoot, root))
        {
            throw new InvalidOperationException(
                $"Project '{normalizedName}' is already registered at '{existingRoot}' and cannot be repointed to '{root}'.");
        }

        var existingBranch = ResolveStoredBranch(existing);
        if (integrationBranch is not null && !string.Equals(existingBranch, branch, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Project '{normalizedName}' is already registered with integration branch '{existingBranch}' and cannot be changed to '{branch}'.");
        }

        alreadyRegistered = true;
        return new OrchestratorProject(existing.Name, existingRoot)
            { IntegrationBranch = existingBranch, DataRootDirectory = DataRootDirectory };
    }

    public void SelectProject(string name)
    {
        var normalizedName = OrchestratorProjectSelection.NormalizeProjectName(name);
        Directory.CreateDirectory(RegistryDirectory);
        if (normalizedName.Equals(OrchestratorWorkspace.DefaultProjectName, StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(CurrentProjectPath))
            {
                File.Delete(CurrentProjectPath);
            }

            return;
        }

        _ = GetRequiredProject(normalizedName);
        File.WriteAllText(CurrentProjectPath, normalizedName + Environment.NewLine);
    }

    public string? ReadSelectedProjectName()
    {
        if (!File.Exists(CurrentProjectPath))
        {
            return null;
        }

        var text = File.ReadAllText(CurrentProjectPath).Trim();
        return string.IsNullOrWhiteSpace(text)
            ? null
            : OrchestratorProjectSelection.NormalizeProjectName(text);
    }

    public OrchestratorProject ResolveActiveProject(string defaultRootDirectory, string? overrideProjectName)
    {
        var selected = overrideProjectName ?? ReadSelectedProjectName();
        if (string.IsNullOrWhiteSpace(selected) ||
            selected.Equals(OrchestratorWorkspace.DefaultProjectName, StringComparison.OrdinalIgnoreCase))
        {
            return new OrchestratorProject(
                OrchestratorWorkspace.DefaultProjectName,
                Path.GetFullPath(defaultRootDirectory));
        }

        return GetRequiredProject(selected);
    }

    public OrchestratorProject GetRequiredProject(string name)
    {
        var normalizedName = OrchestratorProjectSelection.NormalizeProjectName(name);
        if (normalizedName.Equals(OrchestratorWorkspace.DefaultProjectName, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The default project is resolved from the current repository root.");
        }

        var match = ListProjects().FirstOrDefault(project =>
            project.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            throw new ArgumentException($"Unknown project '{normalizedName}'. Create it with: project create {normalizedName} --root <path>");
        }

        return match;
    }

    private static string ResolveStoredBranch(ProjectEntry entry)
    {
        try
        {
            return TrunkBranchName.Resolve(entry.IntegrationBranch);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"Project '{entry.Name}' has an invalid integration branch.", ex);
        }
    }

    private RegistryFile LoadFile()
    {
        if (!File.Exists(RegistryPath))
        {
            return new RegistryFile();
        }

        var file = JsonSerializer.Deserialize<RegistryFile>(File.ReadAllText(RegistryPath), JsonOptions)
            ?? new RegistryFile();
        file.Projects.RemoveAll(project => string.IsNullOrWhiteSpace(project.Name) || string.IsNullOrWhiteSpace(project.RootDirectory));
        return file;
    }

    private void SaveFile(RegistryFile file)
    {
        Directory.CreateDirectory(RegistryDirectory);
        var temporaryPath = RegistryPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(file, JsonOptions));
            File.Move(temporaryPath, RegistryPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed class RegistryFile
    {
        public List<ProjectEntry> Projects { get; set; } = [];
    }

    private sealed class ProjectEntry
    {
        public ProjectEntry(string name, string rootDirectory)
        {
            Name = name;
            RootDirectory = rootDirectory;
        }

        public string Name { get; set; }
        public string RootDirectory { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? IntegrationBranch { get; set; }
    }
}
