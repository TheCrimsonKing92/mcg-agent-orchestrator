using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.Json;

// Parallel-safe: every test writes under its own temp root; holder identity and boot time are injected.
// 2026-10-16: 13 pre-reboot legacy attempts fenced width-1 admission because their recorded PIDs
// (incomplete identity) were reused by unrelated processes, three of them uninspectable services.
public sealed class LegacyAcceptanceHolderHostBootSettlementTests : IDisposable
{
    private static readonly DateTimeOffset Boot = new(2026, 10, 14, 6, 32, 40, TimeSpan.Zero);
    private static readonly DateTimeOffset BeforeBoot = new(2026, 10, 1, 22, 35, 10, TimeSpan.Zero);
    private static readonly DateTimeOffset AfterBoot = Boot.AddHours(4);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"legacy-holder-boot-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void TerminalAttemptPredatingBoot_ReleasesCapacityWithTerminalReceiptAndStaysReleased()
    {
        var legacy = WriteLegacy(ConductorParallelAcceptanceAttemptOutcome.Failed, BeforeBoot);
        var coordinator = Coordinator(() => Boot);

        Assert.Empty(coordinator.GetAllCapacityReservingAttempts());
        Assert.True(LegacyAcceptanceHolderSettlement.HasTerminalReceipt(AcceptanceHolderBinding.For(legacy),
            legacy.OwnerProcessId, null, null, legacy.StartedAt,
            LegacyAcceptanceHolderSettlement.PredatesHostBootEvidence));
        // The conductor's own terminal writes must not re-fence the attempt on the next census.
        Assert.Empty(Coordinator(() => Boot).GetAllCapacityReservingAttempts());
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Failed, Read(legacy).Outcome);
    }

    [Fact]
    public void RunningAttemptPredatingBootWithoutResult_IsTerminalizedBeforeCapacityRelease()
    {
        var legacy = WriteLegacy(ConductorParallelAcceptanceAttemptOutcome.Running, BeforeBoot,
            reconciled: false, writeExitAndResult: false);

        Assert.Empty(Coordinator(() => Boot).GetAllCapacityReservingAttempts());
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.ProcessDied, Read(legacy).Outcome);
    }

    [Theory]
    [InlineData("heartbeat-field")]
    [InlineData("heartbeat-file")]
    [InlineData("exit-file")]
    [InlineData("started")]
    public void ActivityAfterBoot_RetainsTheFence(string afterBoot)
    {
        var legacy = WriteLegacy(ConductorParallelAcceptanceAttemptOutcome.Failed,
            afterBoot == "started" ? AfterBoot : BeforeBoot,
            heartbeatAt: afterBoot == "heartbeat-field" ? AfterBoot : null);
        if (afterBoot == "heartbeat-file") File.SetLastWriteTimeUtc(legacy.HeartbeatPath, AfterBoot.UtcDateTime);
        if (afterBoot == "exit-file") File.SetLastWriteTimeUtc(legacy.ExitCodePath, AfterBoot.UtcDateTime);

        Assert.Single(Coordinator(() => Boot).GetAllCapacityReservingAttempts());
    }

    [Fact]
    public void WithoutBootTime_TheFailClosedLegacyFenceIsUnchanged()
    {
        WriteLegacy(ConductorParallelAcceptanceAttemptOutcome.Failed, BeforeBoot);

        Assert.Single(Coordinator(hostBootTime: null).GetAllCapacityReservingAttempts());
    }

    private ConductorParallelAcceptanceAttemptCoordinator Coordinator(Func<DateTimeOffset?>? hostBootTime) =>
        new(_root, TrunkBranchName.Default,
            utcNow: () => AfterBoot,
            // The recorded PID now belongs to an unrelated process whose identity cannot be read.
            classifyHolder: _ => SpawnTrackedProcessStatus.Unknown,
            isHolderProcessAbsent: _ => false,
            holderLease: new AcceptanceHolderLease(),
            hostBootTime: hostBootTime);

    private ConductorParallelAcceptanceAttempt WriteLegacy(ConductorParallelAcceptanceAttemptOutcome outcome,
        DateTimeOffset startedAt, DateTimeOffset? heartbeatAt = null, bool reconciled = true,
        bool writeExitAndResult = true)
    {
        var directory = Path.Combine(_root, "goal");
        Directory.CreateDirectory(directory);
        var legacy = new ConductorParallelAcceptanceAttempt("legacy", "goal", "goal", 0, "branch", "main",
            startedAt, heartbeatAt ?? startedAt.AddMinutes(20), 24100, outcome,
            Path.Combine(directory, "out"), Path.Combine(directory, "err"),
            Path.Combine(directory, "exit"), Path.Combine(directory, "heartbeat"),
            Path.Combine(directory, "result"), Path.Combine(directory, "legacy.attempt.json"))
        {
            CompletedAt = outcome == ConductorParallelAcceptanceAttemptOutcome.Running ? null : startedAt.AddMinutes(21),
            ReconciledAt = reconciled ? startedAt.AddMinutes(21) : null
        };
        File.WriteAllText(legacy.MetadataPath, JsonSerializer.Serialize(legacy, Json));
        var artifacts = writeExitAndResult
            ? new[] { legacy.StdoutPath, legacy.StderrPath, legacy.HeartbeatPath, legacy.ExitCodePath, legacy.ResultPath }
            : new[] { legacy.StdoutPath, legacy.StderrPath, legacy.HeartbeatPath };
        foreach (var path in artifacts)
        {
            File.WriteAllText(path, path == legacy.ResultPath ? "{}" : "1");
            File.SetLastWriteTimeUtc(path, startedAt.AddMinutes(20).UtcDateTime);
        }
        return legacy;
    }

    private static ConductorParallelAcceptanceAttempt Read(ConductorParallelAcceptanceAttempt legacy) =>
        JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(File.ReadAllText(legacy.MetadataPath), Json)!;
}
