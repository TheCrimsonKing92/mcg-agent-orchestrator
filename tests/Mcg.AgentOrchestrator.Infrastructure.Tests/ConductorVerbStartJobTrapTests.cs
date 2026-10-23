using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: private workspaces, fake process/lock/setup seams, and local writers.
public sealed class ConductorVerbStartJobTrapTests
{
    [Fact]
    public void TrappingJobRefusesBeforeLaunch()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var launcher = new ConductorVerbStartTests.RecordingLauncher();
        var probe = new FixedJobProbe(new(true, 0x00002000));
        using var output = new StringWriter();
        using var error = new StringWriter();
        var setupCalls = 0;
        Assert.False(File.Exists(fixture.Workspace.ConductorStopFilePath));
        Assert.False(File.Exists(Path.Combine(fixture.Workspace.OrchestratorDirectory, "conduct-loop.lock")));

        var exit = CliConductorCommand.Run(["conductor", "start"], fixture.Workspace,
            launcher, new ConductorVerbStartTests.FixedProbe(null), output: output, error: error,
            storeSetup: _ => { setupCalls++; return []; }, jobProbe: probe);

        Assert.Equal(1, exit);
        Assert.Equal("Refused: this shell's job ends its processes when it closes and does not allow breakaway (jobFlags=0x00002000); start the conductor from a terminal outside that job." + Environment.NewLine,
            error.ToString());
        Assert.Equal("", output.ToString());
        Assert.Empty(launcher.Requests);
        Assert.Equal(0, setupCalls);
        Assert.Equal(1, probe.Reads);
    }

    [Fact]
    public void PermissiveJobLaunchesAsToday() => AssertLaunches(new(true, 0x00000800));

    [Fact]
    public void UnknownJobLaunchesAsToday() => AssertLaunches(ProcessJobReading.Unknown);

    [Fact]
    public void TrappingJobPreservesStopFileEvenWhenClearStopRequested()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        File.WriteAllText(fixture.Workspace.ConductorStopFilePath, "operator stop");
        var launcher = new ConductorVerbStartTests.RecordingLauncher();
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, CliConductorCommand.Run(["conductor", "start", "--clear-stop"], fixture.Workspace,
            launcher, new ConductorVerbStartTests.FixedProbe(null), output: output, error: error,
            jobProbe: new FixedJobProbe(new(true, 0x00002000))));
        Assert.Equal("operator stop", File.ReadAllText(fixture.Workspace.ConductorStopFilePath));
        Assert.Empty(launcher.Requests);
    }

    private static void AssertLaunches(ProcessJobReading reading)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var setupCalls = 0;
        var launcher = new ConductorVerbStartTests.RecordingLauncher(() => Assert.Equal(1, setupCalls));
        var probe = new FixedJobProbe(reading);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = CliConductorCommand.Run(["conductor", "start"], fixture.Workspace,
            launcher, new ConductorVerbStartTests.FixedProbe(null), output: output, error: error,
            storeSetup: _ => { setupCalls++; return []; }, jobProbe: probe);
        Assert.Equal(0, exit);
        var request = Assert.Single(launcher.Requests);
        Assert.Equal("pwsh", request.FileName);
        Assert.Equal("Conductor launched: pid 1234; stdout log: stdout.log" + Environment.NewLine +
            "Check status: mcg-orchestrator.cmd conductor status" + Environment.NewLine, output.ToString());
        Assert.Equal("", error.ToString());
        Assert.Equal(1, probe.Reads);
        Assert.Equal(1, setupCalls);
    }

    private sealed class FixedJobProbe(ProcessJobReading reading) : IProcessJobProbe
    {
        internal int Reads { get; private set; }

        public ProcessJobReading Read()
        {
            Reads++;
            return reading;
        }
    }
}
