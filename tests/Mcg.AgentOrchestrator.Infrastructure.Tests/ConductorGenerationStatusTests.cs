using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class ConductorGenerationStatusTests
{
    private const string BuildCommit = "abcdef0123456789abcdef0123456789abcdef01";
    private static readonly DateTimeOffset StartedAt = new(2026, 9, 26, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CaseInsensitiveEqualMainReportsCurrent()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        WriteEvents(fixture.Workspace, $" {BuildCommit} ");
        var main = BuildCommit.ToUpperInvariant();
        var calls = 0;

        var text = ReadStatus(fixture.Workspace, () => { calls++; return $" {main}\r\n"; });

        AssertGeneration(text, $"Generation: current with main {main}");
        Assert.Equal(1, calls);
    }

    [Fact]
    public void DifferentMainReportsDiffers()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        WriteEvents(fixture.Workspace, $" {BuildCommit} ");
        const string main = "123456789abcdef0123456789abcdef0123456789";

        var text = ReadStatus(fixture.Workspace, () => $" {main}\n");

        AssertGeneration(text, $"Generation: differs from main (running {BuildCommit}, main {main})");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n")]
    public void UnavailableMainReportsMainUnavailable(string? main)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        WriteEvents(fixture.Workspace, BuildCommit);

        var text = ReadStatus(fixture.Workspace, () => main);

        AssertGeneration(text, "Generation: main unavailable");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void UnavailableSupervisorBuildDoesNotResolveMain(string? build)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        WriteEvents(fixture.Workspace, build);
        var calls = 0;

        var text = ReadStatus(fixture.Workspace, () => { calls++; return BuildCommit; });

        AssertGeneration(text, "Generation: unavailable (no supervisor build recorded)");
        Assert.Contains($"Supervisor build: {build ?? "unavailable"}{Environment.NewLine}", text);
        Assert.Equal(0, calls);
    }

    private static string ReadStatus(OrchestratorWorkspace workspace, Func<string?> mainCommit)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = CliConductorCommand.Run(["conductor", "status"], workspace,
            lockProbe: new ConductorVerbStartTests.FixedProbe(null), bootTime: () => StartedAt.AddDays(-1),
            mainCommit: mainCommit, output: output, error: error);
        Assert.Equal(0, exit);
        Assert.Equal("", error.ToString());
        return output.ToString();
    }

    private static void AssertGeneration(string text, string expected)
    {
        var lines = text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var supervisor = Assert.Single(lines.Where(line => line.StartsWith("Supervisor build:", StringComparison.Ordinal)));
        Assert.Equal(expected, Assert.Single(lines.Where(line => line.StartsWith("Generation:", StringComparison.Ordinal))));
        Assert.Equal(expected, lines[Array.IndexOf(lines, supervisor) + 1]);
    }

    private static void WriteEvents(OrchestratorWorkspace workspace, string? build)
    {
        Directory.CreateDirectory(workspace.LogDirectory);
        var events = new List<string>
        {
            JsonSerializer.Serialize(new { timestamp = StartedAt, eventKind = "loop-start", goalId = (string?)null, detail = "LOOP_START" })
        };
        if (build is not null)
            events.Add(JsonSerializer.Serialize(new
            {
                timestamp = StartedAt.AddMinutes(10), eventKind = "supervisor-build", goalId = (string?)null,
                detail = "SUPERVISOR_BUILD " + JsonSerializer.Serialize(new { commitSha = build })
            }));
        File.WriteAllText(workspace.ConductEventsLogPath, string.Join("\r\n", events) + "\r\n");
    }
}
