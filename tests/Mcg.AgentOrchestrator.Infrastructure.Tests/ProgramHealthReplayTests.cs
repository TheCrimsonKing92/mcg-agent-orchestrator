using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: unique replay paths, literal receipt evidence and an injected clock.
public sealed class ProgramHealthReplayTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "program-health-replay", Guid.NewGuid().ToString("N"));
    private const string Main = "790560adf35ddea6d9949b873db587ee5a62e6b3";
    private const string FirstId = "merge-train-receipt-6433b3aed445fede4197a47eae6d351f825b847167d86845ae5fb2f48bd5db23";
    private const string SecondId = "merge-train-receipt-71ba107d191cec54a855e8d815f1112f3859767b80ea446b27897726b4c6c809";
    private static DateTimeOffset At(string time) => DateTimeOffset.Parse("2026-10-15T" + time + "Z", CultureInfo.InvariantCulture);

    [Fact]
    public void OvernightHeldTrainsAlertOnTransitionsAndClearOnLanding()
    {
        var clock = new OwnerConsoleTestClock { Now = At("03:40:00") };
        ProgramHealthReceiptObservation[] receipts =
        [
            new(FirstId, At("04:49:51.9783205"), Main, ["4edbbf63355d4bbfb92658e1face8fe5", "69a480fd32864f29a474d741e87a8ecf"]),
            new(SecondId, At("05:06:50.2658607"), Main, ["2017d034a8f742bdb2995fb7d46aaed2", "301be88d6f1f4c03aa8bc1040b7df06d"])
        ];
        var goals = receipts.SelectMany(receipt => receipt.MemberGoalIds).Select(id => (new GoalId(id), GoalStatus.Verified)).ToArray();
        var log = Path.Combine(_root, "events.jsonl");
        var monitor = new ConductorProgramHealthMonitor(Path.Combine(_root, ConductorProgramHealthMonitor.StateFileName),
            () => null, new ConductEventLogWriter(log), clock,
            id => receipts.Where(receipt => receipt.MemberGoalIds.Contains(id.Value)).ToArray());
        JsonElement[] Events() => File.Exists(log) ? File.ReadAllLines(log).Select(line =>
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }).ToArray() : [];
        void Tick(string time, string head = Main)
        {
            clock.Now = At(time);
            monitor.Evaluate(goals, head);
        }

        monitor.Evaluate(goals, "69c83cb09ce2bc7cfd5f30039c1bd0ccab98450a");
        Tick("03:44:07");
        Tick("05:19:00");
        Assert.Empty(Events());
        Tick("05:20:00");
        var first = Assert.Single(Events());
        Assert.Equal("program-health", first.GetProperty("eventKind").GetString());
        Assert.Equal("decision", first.GetProperty("operator").GetString());
        Assert.Equal("PROGRAM_HEALTH_ALERT conditions=held-receipt topBlocker=" + FirstId +
            " minutesSinceLanding=95 lastLanding=2026-10-15T03:44:07Z currentReceipts=2 overThreshold=1 verifiedGoals=4 diagnosis=Nothing landed for 1 h 35 min; 1 passed train held, oldest 6433b3ae for 30 min (4edbbf63,69a480fd).",
            first.GetProperty("detail").GetString());
        for (var minute = 21; minute <= 44; minute++) Tick($"05:{minute}:00");
        Assert.Single(Events());
        Tick("05:45:00");
        var alerts = Events();
        Assert.Equal(2, alerts.Length);
        Assert.Equal("decision", alerts[1].GetProperty("operator").GetString());
        Assert.Equal("PROGRAM_HEALTH_ALERT conditions=held-receipt,no-landing topBlocker=" + FirstId +
            " minutesSinceLanding=120 lastLanding=2026-10-15T03:44:07Z currentReceipts=2 overThreshold=2 verifiedGoals=4 diagnosis=Nothing landed for 2 h 0 min; 2 passed trains held, oldest 6433b3ae for 55 min (4edbbf63,69a480fd).",
            alerts[1].GetProperty("detail").GetString());
        Tick("12:07:04", "c949bef1902c0f2f4994f36ea2f306bcb0df3925");
        var events = Events();
        Assert.Equal(3, events.Length);
        Assert.Equal("outcome", events[2].GetProperty("operator").GetString());
        Assert.Equal("PROGRAM_HEALTH_CLEARED previous=held-receipt,no-landing|" + FirstId +
            " minutesSinceLanding=0 currentReceipts=0 verifiedGoals=4", events[2].GetProperty("detail").GetString());
        Tick("12:08:00", "c949bef1902c0f2f4994f36ea2f306bcb0df3925");
        Assert.Equal(3, Events().Length);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
