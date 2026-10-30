using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Each fixture owns its registry, project artifacts and clock; never runs discovery.
internal sealed class OnboardingConsoleFixture : IDisposable
{
    internal const string TestKey = "commands/declared/test";
    internal const string BuildKey = "commands/declared/build";
    internal static readonly FactSource Source = new("project.yaml", 1);
    private readonly string _root = SharedTestSupport.CreateTempDirectory();
    private readonly List<ConductorLoopLease> _leases = [];
    internal readonly OwnerConsoleTestClock Clock = new()
    { Now = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.FromHours(-5)).AddMilliseconds(456) };
    internal OrchestratorProjectRegistry Registry { get; }
    internal string Home { get; }

    internal OnboardingConsoleFixture()
    {
        Home = Path.Combine(_root, "home");
        Directory.CreateDirectory(Home);
        Registry = new(Path.Combine(_root, "registry"), Path.Combine(_root, "data"));
    }

    internal OrchestratorWorkspace Register(string name)
    {
        var root = Path.Combine(_root, "repos", name);
        Directory.CreateDirectory(root);
        return Registry.CreateProject(name, root, integrationBranch: "main").ResolveWorkspace();
    }

    internal static ProjectModel Model(params string[] keys) => new(ProjectModel.CurrentSchemaVersion, ".",
        [], [], [], keys.Select(key => new ProjectOwnerQuestion(key, "Which command should run?", Source)
            { SuggestedValue = "npm test" }).ToArray(),
        [new("declared", new("npm run build", Source, FactConfidence.High),
            new("npm run old-test", Source, FactConfidence.High))], [], [], [], Stack: ProjectStack.Declared);

    internal static void Store(OrchestratorWorkspace workspace, ProjectModel model)
    {
        Directory.CreateDirectory(workspace.OrchestratorDirectory);
        File.WriteAllText(Path.Combine(workspace.OrchestratorDirectory, "project-model.json"), ProjectModelJson.Serialize(model));
    }

    internal void AnswerAll(OrchestratorWorkspace workspace, params string[] keys) =>
        OwnerAnswerFile.Write(workspace.OrchestratorDirectory,
            keys.Select(key => new OwnerAnswer(key, "npm test", null, Clock.GetUtcNow().ToUniversalTime())).ToArray());

    internal static void InstallManifest(OrchestratorWorkspace workspace) =>
        File.WriteAllText(Path.Combine(workspace.OrchestratorDirectory, "acceptance-manifest.json"),
            "{\"checks\":[{\"type\":\"command\",\"command\":\"npm test\"}]}");

    internal void StartConductor(OrchestratorWorkspace workspace)
    {
        _leases.Add(ConductorLoopLease.Acquire(workspace.OrchestratorDirectory));
        Assert.True(new ConductorLeaseLiveness(workspace.OrchestratorDirectory).IsRunning(), "Fixture must own a live lease.");
    }

    internal static OwnerConsoleBoardProject Describe(OrchestratorWorkspace workspace) =>
        new(workspace.ProjectName, workspace.SqliteStatePath, workspace.PortfolioStorePath,
            OrchestratorDirectory: workspace.OrchestratorDirectory, ConductEventsLogPath: workspace.ConductEventsLogPath);

    internal static ProjectOnboardingState Read(OrchestratorWorkspace workspace) =>
        ProjectOnboardingStateReader.Read(workspace.ProjectName, workspace.OrchestratorDirectory, workspace.ConductEventsLogPath);

    internal static Dictionary<string, string> Snapshot(OrchestratorWorkspace workspace) =>
        Directory.EnumerateFiles(workspace.OrchestratorDirectory, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(workspace.OrchestratorDirectory, path),
                ReadShared, StringComparer.Ordinal);

    private static string ReadShared(string path)
    {
        // The fixture's live lease holds a read/write handle that permits shared readers.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return Convert.ToBase64String(bytes.ToArray());
    }

    public void Dispose()
    {
        foreach (var lease in _leases) lease.Dispose();
        SharedTestSupport.RemoveTempDirectory(_root);
    }
}
