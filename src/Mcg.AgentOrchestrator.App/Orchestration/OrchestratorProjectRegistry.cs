using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

public sealed class OrchestratorProjectRegistry
{
    public const string RegistryHomeEnvironmentVariable = "MCG_ORCHESTRATOR_PROJECT_REGISTRY";
    private const string RegistryFileName = "projects.json";
    private const string CurrentProjectFileName = "current-project.txt";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public OrchestratorProjectRegistry(string registryDirectory)
    {
        RegistryDirectory = Path.GetFullPath(registryDirectory);
    }

    public string RegistryDirectory { get; }
    public string RegistryPath => Path.Combine(RegistryDirectory, RegistryFileName);
    public string CurrentProjectPath => Path.Combine(RegistryDirectory, CurrentProjectFileName);

    public static OrchestratorProjectRegistry CreateDefault()
    {
        var configured = Environment.GetEnvironmentVariable(RegistryHomeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return new OrchestratorProjectRegistry(configured);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = string.IsNullOrWhiteSpace(localAppData)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mcg-agent-orchestrator")
            : Path.Combine(localAppData, "Mcg.AgentOrchestrator");
        return new OrchestratorProjectRegistry(Path.Combine(root, "projects"));
    }

    public IReadOnlyList<OrchestratorProject> ListProjects()
    {
        var file = LoadFile();
        return file.Projects
            .Select(entry => new OrchestratorProject(entry.Name, Path.GetFullPath(entry.RootDirectory)))
            .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public OrchestratorProject CreateProject(string name, string rootDirectory)
    {
        var project = ResolveProjectForCreation(name, rootDirectory, out var alreadyRegistered);
        if (alreadyRegistered)
        {
            return project;
        }

        var file = LoadFile();
        var entry = new ProjectEntry(project.Name, project.RootDirectory);
        file.Projects.Add(entry);
        SaveFile(file);
        return new OrchestratorProject(entry.Name, entry.RootDirectory);
    }

    public OrchestratorProject ResolveProjectForCreation(
        string name,
        string rootDirectory,
        out bool alreadyRegistered)
    {
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
            return new OrchestratorProject(normalizedName, root);
        }

        var existingRoot = Path.GetFullPath(existing.RootDirectory);
        if (!PathsEqual(existingRoot, root))
        {
            throw new InvalidOperationException(
                $"Project '{normalizedName}' is already registered at '{existingRoot}' and cannot be repointed to '{root}'.");
        }

        alreadyRegistered = true;
        return new OrchestratorProject(existing.Name, existingRoot);
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
    }
}
