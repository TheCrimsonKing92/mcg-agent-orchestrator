using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: private workspace files, injected lock/clock/commit seams, local writers.
public sealed class ConductorStatusJobTrapTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
    private const string Warning = "Supervisor job: jobFlags=0x00002000 ends the conductor when the launching app closes; restart it from a terminal outside that job.";

    [Fact]
    public void TrappedSupervisorBuildAddsWarningLine()
    {
        using var trapped = new ConductorVerbStartTests.Fixture();
        using var permissive = new ConductorVerbStartTests.Fixture();
        Seed(trapped.Workspace, """{"commitSha":"abc123","jobMembership":true,"innermostJobLimitFlags":"0x00002000"}""");
        Seed(permissive.Workspace, """{"commitSha":"abc123","jobMembership":true,"innermostJobLimitFlags":"0x00000800"}""");
        var trappedOutput = ReadStatus(trapped.Workspace);
        var lines = trappedOutput.Split(Environment.NewLine);
        var buildIndex = Array.IndexOf(lines, "Supervisor build: abc123");
        Assert.True(buildIndex >= 0);
        Assert.Equal(Warning, lines[buildIndex + 1]);
        var permissiveOutput = ReadStatus(permissive.Workspace);
        Assert.DoesNotContain(permissiveOutput.Split(Environment.NewLine),
            line => line.StartsWith("Supervisor job:", StringComparison.Ordinal));
        Assert.Equal(permissiveOutput, trappedOutput.Replace(Warning + Environment.NewLine, "", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("TRUE")]
    public void StringMembershipAlsoAddsWarning(string membership)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        Seed(fixture.Workspace, JsonSerializer.Serialize(new
        {
            commitSha = "abc123", jobMembership = membership, innermostJobLimitFlags = "0x00002000"
        }));
        Assert.Contains("Supervisor build: abc123" + Environment.NewLine + Warning + Environment.NewLine,
            ReadStatus(fixture.Workspace));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"jobMembership\":\"unknown\",\"innermostJobLimitFlags\":\"0x00002000\"}")]
    [InlineData("{\"jobMembership\":false,\"innermostJobLimitFlags\":\"0x00002000\"}")]
    [InlineData("{\"jobMembership\":true}")]
    [InlineData("{\"jobMembership\":true,\"innermostJobLimitFlags\":null}")]
    [InlineData("{\"jobMembership\":true,\"innermostJobLimitFlags\":8192}")]
    [InlineData("{\"jobMembership\":true,\"innermostJobLimitFlags\":\"0x2000\"}")]
    [InlineData("{\"jobMembership\":true,\"innermostJobLimitFlags\":\"0x0000200Z\"}")]
    [InlineData("{\"jobMembership\":[],\"innermostJobLimitFlags\":\"0x00002000\"}")]
    [InlineData("{malformed")]
    public void MissingOrInvalidMetadataPreservesOutput(string metadata)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        Seed(fixture.Workspace, "{}");
        var baseline = ReadStatus(fixture.Workspace);
        Seed(fixture.Workspace, metadata);
        Assert.Equal(baseline, ReadStatus(fixture.Workspace));
    }

    [Fact]
    public void LatestBuildReplacesOlderTrapReading()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        Seed(fixture.Workspace, """{"commitSha":"abc123","jobMembership":true,"innermostJobLimitFlags":"0x00002000"}""");
        Assert.Contains(Warning, ReadStatus(fixture.Workspace));
        File.AppendAllText(fixture.Workspace.ConductEventsLogPath, JsonSerializer.Serialize(new
        {
            timestamp = At.AddMinutes(1), eventKind = "supervisor-build",
            detail = "SUPERVISOR_BUILD {\"commitSha\":\"new-build\"}"
        }) + Environment.NewLine);
        var output = ReadStatus(fixture.Workspace);
        Assert.Contains("Supervisor build: new-build", output);
        Assert.DoesNotContain("Supervisor job:", output);
    }

    private static void Seed(OrchestratorWorkspace workspace, string metadata)
    {
        Directory.CreateDirectory(workspace.LogDirectory);
        File.WriteAllText(workspace.ConductEventsLogPath, JsonSerializer.Serialize(new
        {
            timestamp = At, eventKind = "supervisor-build", detail = "SUPERVISOR_BUILD " + metadata
        }) + Environment.NewLine);
    }

    private static string ReadStatus(OrchestratorWorkspace workspace)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, CliConductorCommand.Run(["conductor", "status"], workspace,
            lockProbe: new ConductorVerbStartTests.FixedProbe(null), bootTime: () => At.AddDays(-1),
            mainCommit: () => "abc123", output: output, error: error));
        Assert.Equal("", error.ToString());
        return output.ToString();
    }
}
