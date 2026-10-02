using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class ConductorRotatedGenerationStatusTests
{
    private const string BuildCommit = "abcdef0123456789abcdef0123456789abcdef01";
    private const string OlderName = "conduct-events-20261002120000.log";
    private const string NewerName = "conduct-events-20261002152748.log";
    private static readonly DateTimeOffset StartedAt = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Status_RecoversLifecycleAndBuildFromRotatedGeneration()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        WriteLive(fixture.Workspace, Event(StartedAt.AddHours(6), "goal", "GOAL"));
        WriteRotated(fixture.Workspace, NewerName,
            Event(StartedAt, "loop-start", "LOOP_START"), Build(StartedAt.AddMinutes(10), BuildCommit));

        var text = ReadStatus(fixture.Workspace);

        AssertLine(text, $"Running since {StartedAt:O}");
        AssertLine(text, $"Supervisor build: {BuildCommit}");
        AssertLine(text, $"Generation: current with main {BuildCommit}");
    }

    [Fact]
    public void Status_UsesNewestGenerationNameInsteadOfMtimeOrEventTimestamp()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        WriteLive(fixture.Workspace);
        var older = WriteRotated(fixture.Workspace, OlderName, Build(StartedAt.AddHours(2), "older-build"));
        var newer = WriteRotated(fixture.Workspace, NewerName, Build(StartedAt, BuildCommit));
        File.SetLastWriteTimeUtc(older, StartedAt.AddDays(1).UtcDateTime);
        File.SetLastWriteTimeUtc(newer, StartedAt.UtcDateTime);

        var text = ReadStatus(fixture.Workspace);

        AssertLine(text, $"Supervisor build: {BuildCommit}");
        AssertLine(text, $"Generation: current with main {BuildCommit}");
    }

    [Fact]
    public void Status_LiveStopWinsOverLaterRotatedStartWhileBuildFallsBack()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        WriteLive(fixture.Workspace, Event(StartedAt, "loop-stop", "LOOP_STOP reason=operator-stop\r"));
        WriteRotated(fixture.Workspace, NewerName,
            Event(StartedAt.AddHours(1), "loop-start", "LOOP_START"), Build(StartedAt, BuildCommit));

        var text = ReadStatus(fixture.Workspace);

        AssertLine(text, $"Stopped since {StartedAt:O}; reason=operator-stop");
        Assert.DoesNotContain("Running since ", text);
        AssertLine(text, $"Supervisor build: {BuildCommit}");
    }

    [Theory]
    [InlineData("conduct-events-archive.jsonl")]
    [InlineData("other-events-20261002152748.log")]
    [InlineData("conduct-events-2026100215274.log")]
    [InlineData("conduct-events-20261002152748-x.log")]
    [InlineData("conduct-events-20261002152748.log.pending.jsonl")]
    [InlineData("pending-events/conduct-events-20261002152748.log")]
    public void Status_IgnoresNonGenerationFilesAndSubdirectories(string name)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        WriteLive(fixture.Workspace);
        var ignored = WriteRotated(fixture.Workspace, name,
            Event(StartedAt, "loop-start", "LOOP_START"), Build(StartedAt, BuildCommit));

        var text = ReadStatus(fixture.Workspace);

        AssertLine(text, "Since: unavailable");
        AssertLine(text, "Supervisor build: unavailable");
        AssertLine(text, "Generation: unavailable (no supervisor build recorded)");

        File.Move(ignored, Path.Combine(fixture.Workspace.LogDirectory, NewerName));
        var recognized = ReadStatus(fixture.Workspace);
        AssertLine(recognized, $"Running since {StartedAt:O}");
        AssertLine(recognized, $"Supervisor build: {BuildCommit}");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("live-build")]
    public void Status_LiveBuildEventSuppressesFallbackEvenWithUnavailableCommit(string? commit)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        WriteLive(fixture.Workspace, Build(StartedAt, commit));
        WriteRotated(fixture.Workspace, NewerName,
            Event(StartedAt, "loop-start", "LOOP_START"), Build(StartedAt.AddHours(1), BuildCommit));
        var mainCalls = 0;

        var text = ReadStatus(fixture.Workspace, mainCommit: () => { mainCalls++; return BuildCommit; });

        AssertLine(text, $"Running since {StartedAt:O}");
        AssertLine(text, $"Supervisor build: {commit ?? "unavailable"}");
        Assert.Equal(string.IsNullOrWhiteSpace(commit) ? 0 : 1, mainCalls);
        AssertLine(text, string.IsNullOrWhiteSpace(commit)
            ? "Generation: unavailable (no supervisor build recorded)"
            : $"Generation: differs from main (running {commit}, main {BuildCommit})");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-json\r\n{}\r\n")]
    public void Status_AbsentEmptyOrMalformedLiveLogFallsBack(string? liveContent)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        WriteRotated(fixture.Workspace, NewerName,
            Event(StartedAt, "loop-start", "LOOP_START"), Build(StartedAt, BuildCommit));
        if (liveContent is not null) File.WriteAllText(fixture.Workspace.ConductEventsLogPath, liveContent);

        var text = ReadStatus(fixture.Workspace);

        AssertLine(text, $"Running since {StartedAt:O}");
        AssertLine(text, $"Supervisor build: {BuildCommit}");
    }

    [Fact]
    public void Status_OrdersCollisionSuffixesNumericallyAfterBareGeneration()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        WriteLive(fixture.Workspace);
        WriteRotated(fixture.Workspace, NewerName, Build(StartedAt, "bare-build"));
        WriteRotated(fixture.Workspace, "conduct-events-20261002152748-1.log", Build(StartedAt, "first-build"));
        WriteRotated(fixture.Workspace, "conduct-events-20261002152748-2.log", Build(StartedAt, "second-build"));
        WriteRotated(fixture.Workspace, "CONDUCT-EVENTS-20261002152748-10.LOG", Build(StartedAt, BuildCommit));

        AssertLine(ReadStatus(fixture.Workspace), $"Supervisor build: {BuildCommit}");
    }

    [Fact]
    public void Status_ResolvesKindsIndependentlyAndSelectsLatestEventWithinFirstMatch()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        WriteLive(fixture.Workspace);
        WriteRotated(fixture.Workspace, "conduct-events-20261002160000.log", "not-json", "{}");
        WriteRotated(fixture.Workspace, NewerName,
            Build(StartedAt.AddMinutes(10), BuildCommit), "not-json", Build(StartedAt, "older-build"),
            Event(StartedAt.AddHours(6), "tick", "TICK"));
        var stop = StartedAt.AddMinutes(20);
        WriteRotated(fixture.Workspace, OlderName,
            Event(stop, "loop-stop", "LOOP_STOP reason=operator-stop"),
            Event(StartedAt, "loop-start", "LOOP_START"), Build(StartedAt.AddHours(2), "older-file-build"));

        var text = ReadStatus(fixture.Workspace);

        AssertLine(text, $"Stopped since {stop:O}; reason=operator-stop");
        AssertLine(text, $"Supervisor build: {BuildCommit}");
        AssertLine(text, "Latest tick: unavailable");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Status_PreservesHostRestartRuleForRecoveredLifecycle(bool liveEventAfterBoot)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        WriteLive(fixture.Workspace);
        if (liveEventAfterBoot)
            WriteLive(fixture.Workspace, Event(StartedAt.AddHours(2), "goal", "GOAL"));
        WriteRotated(fixture.Workspace, NewerName, Event(StartedAt, "loop-start", "LOOP_START"));
        var boot = StartedAt.AddHours(1);

        var text = ReadStatus(fixture.Workspace, boot);

        AssertLine(text, liveEventAfterBoot
            ? $"Running since {StartedAt:O}"
            : $"stopped since host restart at {boot:O}");
    }

    [Fact]
    public void Status_SkipsUnreadableGenerationAndContinuesToOlderFile()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        WriteLive(fixture.Workspace);
        var newer = WriteRotated(fixture.Workspace, NewerName, Build(StartedAt, "locked-build"));
        WriteRotated(fixture.Workspace, OlderName,
            Event(StartedAt, "loop-start", "LOOP_START"), Build(StartedAt, BuildCommit));
        using var heldFile = new FileStream(newer, FileMode.Open, FileAccess.Read, FileShare.None);

        var text = ReadStatus(fixture.Workspace);

        AssertLine(text, $"Running since {StartedAt:O}");
        AssertLine(text, $"Supervisor build: {BuildCommit}");
    }

    private static string ReadStatus(OrchestratorWorkspace workspace, DateTimeOffset? boot = null,
        Func<string?>? mainCommit = null)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = CliConductorCommand.Run(["conductor", "status"], workspace,
            lockProbe: new ConductorVerbStartTests.FixedProbe(null), bootTime: () => boot ?? StartedAt.AddDays(-1),
            mainCommit: mainCommit ?? (() => BuildCommit), output: output, error: error);
        Assert.Equal(0, exit);
        Assert.Equal("", error.ToString());
        return output.ToString();
    }

    private static void AssertLine(string text, string expected) =>
        Assert.Contains(expected, text.Split(Environment.NewLine));

    private static string Event(DateTimeOffset at, string kind, string detail) =>
        JsonSerializer.Serialize(new { timestamp = at, eventKind = kind, goalId = (string?)null, detail });

    private static string Build(DateTimeOffset at, string? commit) =>
        Event(at, "supervisor-build", "SUPERVISOR_BUILD " + JsonSerializer.Serialize(new { commitSha = commit }));

    private static void WriteLive(OrchestratorWorkspace workspace, params string[] events)
    {
        Directory.CreateDirectory(workspace.LogDirectory);
        File.WriteAllText(workspace.ConductEventsLogPath, string.Join("\r\n", events));
    }

    private static string WriteRotated(OrchestratorWorkspace workspace, string name, params string[] events)
    {
        var path = Path.Combine(workspace.LogDirectory, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join("\r\n", events));
        return path;
    }
}
