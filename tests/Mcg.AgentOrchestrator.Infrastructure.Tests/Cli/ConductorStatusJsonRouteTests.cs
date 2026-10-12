using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: isolated workspace and local writers; rejected forms must never reach the injected probe.
public sealed class ConductorStatusJsonRouteTests
{
    [Theory]
    [InlineData("conductor", "status")]
    [InlineData("CONDUCTOR", "STATUS")]
    [InlineData("ConDuctor", "sTaTuS")]
    public void ExactJsonForm_MatchesWithCaseInsensitiveVerbs(string command, string verb)
    {
        Assert.True(CliConductorStatusJsonQueryCommand.IsConductorStatusJsonCommand([command, verb, "--json"]));
        CliCommandHelp.ThrowIfInvalidFlags([command, verb, "--json"]);
    }

    [Fact]
    public void OtherForms_DoNotMatchJsonRead()
    {
        string[][] forms =
        [
            ["conductor", "status"],
            ["conductor", "status", "--json", "extra"],
            ["conductor", "status", "--jsonx"],
            ["conductor", "start", "--json"],
            ["conductor", "stop", "--json"],
            ["conductor", "status", "--JSON"],
            ["conductor", "status", "--json", "--help"],
            ["conductor", "status", "--help"]
        ];
        Assert.All(forms, form => Assert.False(CliConductorStatusJsonQueryCommand.IsConductorStatusJsonCommand(form)));
    }

    [Theory]
    [InlineData("status", "--json", "extra")]
    [InlineData("status", "--jsonx", null)]
    [InlineData("start", "--json", null)]
    [InlineData("stop", "--json", null)]
    [InlineData("status", "--JSON", null)]
    public void InvalidForms_ReturnUsageBeforeProbeOrActions(string verb, string flag, string? extra)
    {
        using var fixture = new Fixture();
        using var output = new StringWriter();
        using var error = new StringWriter();
        string[] args = extra is null ? ["conductor", verb, flag] : ["conductor", verb, flag, extra];
        Assert.Equal(1, CliConductorCommand.Run(args, fixture.Workspace, lockProbe: new RejectProbe(),
            storeSetup: _ => throw new Xunit.Sdk.XunitException("Invalid form reached store setup."),
            output: output, error: error));
        Assert.Equal("", output.ToString());
        Assert.Equal(CliCommandHelp.ConductorUsage + Environment.NewLine, error.ToString());
        Assert.Empty(Directory.EnumerateFiles(fixture.Workspace.RootDirectory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void FlagsAndClassification_AdmitOnlyExactReadAndKeepExecutionCapability()
    {
        CliCommandHelp.ThrowIfInvalidFlags(["conductor", "status", "--json"]);
        Assert.Throws<ArgumentException>(() => CliCommandHelp.ThrowIfInvalidFlags(["conductor", "start", "--json"]));
        Assert.Throws<ArgumentException>(() => CliCommandHelp.ThrowIfInvalidFlags(["conductor", "stop", "--json"]));
        Assert.Throws<ArgumentException>(() => CliCommandHelp.ThrowIfInvalidFlags(["conductor", "status", "--jsonx"]));
        Assert.Throws<ArgumentException>(() => CliCommandHelp.ThrowIfInvalidFlags(["conductor", "status", "--json", "extra"]));
        Assert.Equal(CliCommandCapability.Execution, CliCommandCapabilities.Classify(["conductor", "status"]));
        Assert.Equal(CliCommandCapabilities.Classify(["conductor", "status"]),
            CliCommandCapabilities.Classify(["conductor", "status", "--json"]));
        Assert.Contains("conductor status [--json]", CliCommandHelp.ConductorUsage);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "conductor-status-" + Guid.NewGuid().ToString("N"));
        internal OrchestratorWorkspace Workspace { get; }

        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            Workspace = OrchestratorWorkspace.ForDirectory(_root);
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    private sealed class RejectProbe : IConductorLockProbe
    {
        public int? ActiveOwnerPid(OrchestratorWorkspace workspace) =>
            throw new Xunit.Sdk.XunitException("Invalid form reached the lock probe.");
    }
}
