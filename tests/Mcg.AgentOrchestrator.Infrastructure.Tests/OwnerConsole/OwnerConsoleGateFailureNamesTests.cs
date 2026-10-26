// Parallel-safe: TRX files live in a unique test-owned folder.
using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class OwnerConsoleGateFailureNamesTests
{
    private const string Prefix = "ca798e7c-gate-20261016123751767-1a60276764bb4900b24d041376df0054";
    private const string Target = "infrastructure tests: Remainder";
    private static readonly DateTimeOffset Now = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    private const string Trx = """
        <TestRun><Results>
        <UnitTestResult testName="Sample.Suite.AlphaCase.FailsOnPurpose" outcome="Failed">
        <Output><ErrorInfo><Message>first failure</Message></ErrorInfo></Output></UnitTestResult>
        <UnitTestResult testName="Sample.Suite.BetaCase.AlsoFails" outcome="Failed">
        <Output><ErrorInfo><Message>second failure</Message></ErrorInfo></Output></UnitTestResult>
        <UnitTestResult testName="Sample.Suite.PassingCase.Passes" outcome="Passed"/>
        </Results></TestRun>
        """;

    [Fact]
    public void HeartbeatAndRerunNames_MapToVerifierTrxAndShortenOnlyNames()
    {
        InFolder(directory =>
        {
            var path = TrxPath(directory);
            File.WriteAllText(path, Trx);
            var reader = new OwnerConsoleGateFailureNames();
            var lane = Lane(directory);
            var names = reader.Read(lane);
            Assert.Equal(new[] { "AlphaCase.FailsOnPurpose", "BetaCase.AlsoFails" }, names);
            Assert.Same(names, reader.Read(lane));
            Assert.Same(names, reader.Read(lane with { HeartbeatPath = Path.Combine(directory, Prefix + ".remainder-hash-run-1.gate-heartbeat.json") }));
            File.WriteAllText(path, Trx.Replace("BetaCase.AlsoFails", "DifferentCase.AlsoFails"));
            Assert.Equal(new[] { "AlphaCase.FailsOnPurpose", "DifferentCase.AlsoFails" }, reader.Read(lane));
        });
    }

    [Fact]
    public void MissingMalformedOrUnusablePaths_ReturnEmptyAndRetryWhenFileAppears()
    {
        InFolder(directory =>
        {
            var reader = new OwnerConsoleGateFailureNames();
            var lane = Lane(directory);
            Assert.Empty(reader.Read(lane));
            Assert.Empty(reader.Read(lane with { HeartbeatPath = null }));
            Assert.Empty(reader.Read(lane with { Target = null }));
            Assert.Empty(reader.Read(lane with { HeartbeatPath = "invalid\0path" }));
            File.WriteAllText(TrxPath(directory), "not XML");
            Assert.Empty(reader.Read(lane));
            File.WriteAllText(TrxPath(directory), Trx);
            Assert.Equal(2, reader.Read(lane).Count);
            File.Delete(TrxPath(directory));
            Assert.Empty(reader.Read(lane));
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Loader_WiresFailedNamesAndHostActivityIntoLiveAndIdleSnapshots(bool live)
    {
        InFolder(directory =>
        {
            var log = Path.Combine(directory, "conduct-events.log");
            var manifest = Path.Combine(directory, "manifest.json");
            File.WriteAllText(manifest, """{"engine":{"infrastructureTestLanes":[{"name":"Remainder","estimatedSerialSeconds":970}]}}""");
            File.WriteAllText(TrxPath(directory), Trx);
            var progress = JsonSerializer.Serialize(new { timestamp = Now.AddSeconds(-10), eventKind = "gate-progress", goalId = "ca798e7c",
                detail = $"PHASE_PROGRESS goal=ca798e7c phase=check-complete target=\"{Target}\" elapsed_ms=1091000 outcome=failed heartbeat=\"{Lane(directory).HeartbeatPath}\"" });
            var evidence = JsonSerializer.Serialize(new { timestamp = Now.AddMinutes(-5), eventKind = "EVIDENCE_START", goalId = "c8cfc17e",
                attempt = "e1", detail = (string?)null, member_requests = new[] { "Infrastructure.Tests:SampleTests" } });
            File.WriteAllText(log, "malformed\n" + (live ? progress + "\n" : "") + evidence);
            var loader = new OwnerConsoleGateProgressLoader(log, manifest, Path.Combine(directory, "missing-history.jsonl"),
                new OwnerConsoleTestClock { Now = Now });
            var gate = Assert.IsType<OwnerConsoleGateProgress>(loader.Load(new Dictionary<string, string>()));
            Assert.Equal(live, gate.Live);
            Assert.Equal("SampleTests", Assert.Single(gate.HostActivity!).Label);
            Assert.Equal(TimeSpan.FromMinutes(5), Assert.Single(gate.HostActivity!).Age);
            if (live)
            {
                var lane = Assert.Single(gate.Lanes);
                Assert.Equal(Lane(directory).HeartbeatPath, lane.HeartbeatPath);
                Assert.Equal(new[] { "AlphaCase.FailsOnPurpose", "BetaCase.AlsoFails" }, lane.FailingTestNames);
            }
        });
    }

    private static OwnerConsoleGateLane Lane(string directory) => new("Remainder", OwnerConsoleGateLaneState.Done, "failed", "local",
        TimeSpan.FromSeconds(1091), 970, false, Target: Target,
        HeartbeatPath: Path.Combine(directory, Prefix + ".remainder-hash.gate-heartbeat.json"));
    private static string TrxPath(string directory) => Path.Combine(directory,
        AcceptanceArtifactFileNames.BoundFileName(AcceptanceArtifactFileNames.SanitizeFileName(
            Prefix + "." + AcceptanceArtifactFileNames.Slug(Target)), ".trx"));
    private static void InFolder(Action<string> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "gate-names-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { action(directory); }
        finally { Directory.Delete(directory, true); }
    }
}
