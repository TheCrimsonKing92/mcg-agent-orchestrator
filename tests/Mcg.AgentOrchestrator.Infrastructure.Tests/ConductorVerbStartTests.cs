using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: private workspace directories and injected process/lock/setup seams.
public sealed class ConductorVerbStartTests
{
    [Fact]
    public void StartRequestsExactLauncherContractAndPrintsReceipt()
    {
        using var fixture = new Fixture();
        var launcher = new RecordingLauncher();
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = CliConductorCommand.Run(["conductor", "start"], fixture.Workspace,
            launcher, new FixedProbe(null), output: output, error: error,
            home: OrchestratorHome.Resolve(fixture.Workspace.RootDirectory, _ => null));
        Assert.Equal(0, exit);
        var request = Assert.Single(launcher.Requests);
        Assert.Equal("pwsh", request.FileName);
        Assert.Equal(["-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
            Path.Combine(fixture.Workspace.ExecutionDirectory, "scripts", "Start-OrchestratorCommand.ps1"),
            "-Name", "conduct-loop-daemon", "conduct", "--loop", "--daemon", "--watch",
            "--poll-seconds", "120", "--max-duration", "43200"], request.Arguments);
        Assert.Equal("120", request.Environment["MCG_DISPATCH_MAX_RUNTIME_MIN"]);
        Assert.Contains(CliProtectedProcessEnvironment.ProtectedPidVariable,
            request.EnvironmentVariablesToRemove);
        Assert.Contains("pid 1234", output.ToString());
        Assert.Contains("stdout.log", output.ToString());
        Assert.Contains("conductor status", output.ToString());
        Assert.Equal("", error.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartUsesHomeLauncherAndPreservesProjectSelection(bool projectScoped)
    {
        using var fixture = new Fixture();
        var home = OrchestratorHome.Resolve(fixture.Workspace.RootDirectory + "-home", _ => null);
        var workspace = projectScoped
            ? OrchestratorWorkspace.ForProject("alpha", fixture.Workspace.RootDirectory)
            : fixture.Workspace;
        var launcher = new RecordingLauncher();
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, CliConductorCommand.Run(["conductor", "start"], workspace,
            launcher, new FixedProbe(null), output: output, error: error, home: home));
        var request = Assert.Single(launcher.Requests);
        Assert.Equal(Path.Combine(home.RootDirectory, "scripts", "Start-OrchestratorCommand.ps1"),
            request.Arguments[4]);
        Assert.Equal(workspace.ExecutionDirectory, request.WorkingDirectory);
        Assert.Equal(projectScoped ? ["--project=alpha"] : Array.Empty<string>(),
            request.Arguments.Where(arg => arg.StartsWith("--project", StringComparison.Ordinal)).ToArray());
    }

    [Fact]
    public void ActiveOwnerAndStopFileRefuseThenClearStopAllowsOneLaunch()
    {
        using var fixture = new Fixture();
        var launcher = new RecordingLauncher();
        using var error = new StringWriter();
        Assert.NotEqual(0, CliConductorCommand.Run(["conductor", "start"], fixture.Workspace,
            launcher, new FixedProbe(9876), error: error,
            home: OrchestratorHome.Resolve(fixture.Workspace.RootDirectory, _ => null)));
        Assert.Contains("9876", error.ToString());
        Assert.Empty(launcher.Requests);

        var stopFile = Path.Combine(fixture.Workspace.ExecutionDirectory, ".conduct-stop");
        File.WriteAllText(stopFile, "");
        error.GetStringBuilder().Clear();
        Assert.NotEqual(0, CliConductorCommand.Run(["conductor", "start"], fixture.Workspace,
            launcher, new FixedProbe(null), error: error,
            home: OrchestratorHome.Resolve(fixture.Workspace.RootDirectory, _ => null)));
        Assert.Contains(stopFile, error.ToString());
        Assert.True(File.Exists(stopFile));
        Assert.Empty(launcher.Requests);

        Assert.Equal(0, CliConductorCommand.Run(["conductor", "start", "--clear-stop"], fixture.Workspace,
            launcher, new FixedProbe(null), error: error,
            home: OrchestratorHome.Resolve(fixture.Workspace.RootDirectory, _ => null)));
        Assert.False(File.Exists(stopFile));
        Assert.Single(launcher.Requests);
    }

    [Fact]
    public void Start_EmptyWorkspace_CompletesSetupBeforeLaunch()
    {
        using var fixture = new Fixture();
        var workspace = fixture.Workspace;
        Assert.False(File.Exists(workspace.PortfolioStorePath));
        var calls = new List<string>();
        var launcher = new RecordingLauncher(() =>
        {
            calls.Add("launch");
            Assert.True(File.Exists(workspace.PortfolioStorePath));
            Assert.NotNull(PortfolioStore.OpenReadOnly(workspace.PortfolioStorePath));
        });
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(0, CliConductorCommand.Run(["conductor", "start"], workspace,
            launcher, new FixedProbe(null), output: output, error: error,
            home: OrchestratorHome.Resolve(workspace.RootDirectory, _ => null), storeSetup: directory =>
            {
                Assert.Equal(workspace.OrchestratorDirectory, directory);
                var results = StoreSetupRunner.Run(directory);
                var result = Assert.Single(results);
                Assert.Equal(workspace.PortfolioStorePath, result.DatabasePath);
                Assert.Equal(1, result.Version);
                calls.Add("setup");
                return results;
            }));

        Assert.Equal(["setup", "launch"], calls);
        Assert.Single(launcher.Requests);
        Assert.Equal("", error.ToString());
        Assert.DoesNotContain("portfolio:", output.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Start_SetupFails_ReportsErrorWithoutLaunching(bool sqlite)
    {
        using var fixture = new Fixture();
        var calls = new List<string>();
        var launcher = new RecordingLauncher(() => calls.Add("launch"));
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(1, CliConductorCommand.Run(["conductor", "start"], fixture.Workspace,
            launcher, new FixedProbe(null), output: output, error: error,
            home: OrchestratorHome.Resolve(fixture.Workspace.RootDirectory, _ => null), storeSetup: directory =>
            {
                Assert.Equal(fixture.Workspace.OrchestratorDirectory, directory);
                calls.Add("setup");
                if (sqlite)
                    throw new SqliteException("setup failed", 11);
                throw new InvalidOperationException("setup failed");
            }));

        Assert.Equal(["setup"], calls);
        Assert.Empty(launcher.Requests);
        Assert.Equal($"Error: setup failed{Environment.NewLine}", error.ToString());
        Assert.Equal("", output.ToString());
    }

    internal sealed class RecordingLauncher(Action? onLaunch = null) : IConductorProcessLauncher
    {
        internal List<ConductorLaunchRequest> Requests { get; } = [];
        public ConductorLaunchResult Launch(ConductorLaunchRequest request)
        {
            onLaunch?.Invoke();
            Requests.Add(request);
            return new(0, "{\"pid\":1234,\"stdoutPath\":\"stdout.log\"}", null);
        }
    }

    internal sealed class FixedProbe(int? owner) : IConductorLockProbe
    {
        public int? ActiveOwnerPid(OrchestratorWorkspace workspace) => owner;
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "conductor-verb-" + Guid.NewGuid().ToString("N"));
        internal OrchestratorWorkspace Workspace { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            Workspace = OrchestratorWorkspace.ForDirectory(_root);
        }
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
