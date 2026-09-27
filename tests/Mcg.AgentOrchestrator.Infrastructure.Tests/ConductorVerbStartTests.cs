using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;

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
            launcher, new FixedProbe(null), output: output, error: error);
        Assert.Equal(0, exit);
        var request = Assert.Single(launcher.Requests);
        Assert.Equal("pwsh", request.FileName);
        Assert.Equal(["-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
            Path.Combine(fixture.Workspace.ExecutionDirectory, "scripts", "Start-OrchestratorCommand.ps1"),
            "-Name", "conduct-loop-daemon", "conduct", "--loop", "--daemon", "--watch",
            "--poll-seconds", "120", "--max-duration", "43200"], request.Arguments);
        Assert.Equal("120", request.Environment["MCG_DISPATCH_MAX_RUNTIME_MIN"]);
        Assert.Contains("pid 1234", output.ToString());
        Assert.Contains("stdout.log", output.ToString());
        Assert.Contains("conductor status", output.ToString());
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public void ActiveOwnerAndStopFileRefuseThenClearStopAllowsOneLaunch()
    {
        using var fixture = new Fixture();
        var launcher = new RecordingLauncher();
        using var error = new StringWriter();
        Assert.NotEqual(0, CliConductorCommand.Run(["conductor", "start"], fixture.Workspace,
            launcher, new FixedProbe(9876), error: error));
        Assert.Contains("9876", error.ToString());
        Assert.Empty(launcher.Requests);

        var stopFile = Path.Combine(fixture.Workspace.ExecutionDirectory, ".conduct-stop");
        File.WriteAllText(stopFile, "");
        error.GetStringBuilder().Clear();
        Assert.NotEqual(0, CliConductorCommand.Run(["conductor", "start"], fixture.Workspace,
            launcher, new FixedProbe(null), error: error));
        Assert.Contains(stopFile, error.ToString());
        Assert.True(File.Exists(stopFile));
        Assert.Empty(launcher.Requests);

        Assert.Equal(0, CliConductorCommand.Run(["conductor", "start", "--clear-stop"], fixture.Workspace,
            launcher, new FixedProbe(null), error: error));
        Assert.False(File.Exists(stopFile));
        Assert.Single(launcher.Requests);
    }

    internal sealed class RecordingLauncher : IConductorProcessLauncher
    {
        internal List<ConductorLaunchRequest> Requests { get; } = [];
        public ConductorLaunchResult Launch(ConductorLaunchRequest request)
        {
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
