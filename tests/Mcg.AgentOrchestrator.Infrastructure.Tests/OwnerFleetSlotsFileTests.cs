using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Infrastructure;
using Terminal.Gui.Text;
using Harness = ExecutorSettingIntentScenarioTests.Harness;

// Parallel-safe: fixture-owned configuration and ledger, fixed clock.
public sealed class OwnerFleetSlotsFileTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LatestSyncRowAppearsImmediatelyAfterSlots(bool synced)
    {
        using var fixture = new Harness();
        var row = Row(synced ? "synced" : "failed", Harness.At);
        RemoteExecutorSlotsSyncLedger.Append(Ledger(fixture), row);
        var model = Load(fixture);
        var executor = Assert.Single(model.Executors, node => node.Id == "mcg-exec-1");
        Assert.Equal(row with { ObservedAt = TimeZoneInfo.ConvertTime(row.ObservedAt, TimeZoneInfo.Local) }, executor.SlotsFile);
        var text = OwnerFleetDiagram.Render(model, 100);
        var lines = text.Split('\n');
        var index = Array.FindIndex(lines, line => line.Contains("slots: 3 configured", StringComparison.Ordinal));
        Assert.True(index >= 0);
        var prefix = synced ? "slots file: synced 5" : "slots file: FAILED unreachable";
        Assert.Contains(prefix, lines[index + 1]);
        Assert.Contains(TimeZoneInfo.ConvertTime(Harness.At, TimeZoneInfo.Local)
            .ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture), lines[index + 1]);
        Assert.Single(lines.Where(line => line.Contains("slots file:", StringComparison.Ordinal)));
    }

    [Fact]
    public void AppendOrderSelectsNewestReadableRowAndIgnoresMalformedLines()
    {
        using var fixture = new Harness();
        RemoteExecutorSlotsSyncLedger.Append(Ledger(fixture), Row("failed", Harness.At.AddMinutes(-1)));
        File.AppendAllLines(Ledger(fixture), ["", "{", "null", "{}", """{"executor_id":17}"""]);
        var newest = Row("synced", Harness.At);
        RemoteExecutorSlotsSyncLedger.Append(Ledger(fixture), newest);
        File.AppendAllLines(Ledger(fixture), ["not json"]);
        Assert.Equal(newest, Assert.Single(RemoteExecutorSlotsSyncLedger.ReadLatestByExecutor(Ledger(fixture))).Value);
        var text = OwnerFleetDiagram.Render(Load(fixture), 100);
        Assert.Contains("slots file: synced 5", text);
        Assert.DoesNotContain("slots file: FAILED", text);
        Assert.Single(text.Split('\n').Where(line => line.Contains("slots file:", StringComparison.Ordinal)));
    }

    [Fact]
    public void MissingLedgerPreservesBaselineDiagram()
    {
        using var fixture = new Harness();
        Assert.Empty(RemoteExecutorSlotsSyncLedger.ReadLatestByExecutor(Ledger(fixture)));
        var report = RemoteExecutorReportReader.Read(fixture.Workspace, null, 10, Harness.At.AddHours(-24));
        var baseline = OwnerFleetViewModelBuilder.Build(report, true, TimeZoneInfo.Local);
        Assert.Equal(OwnerFleetDiagram.Render(baseline), OwnerFleetDiagram.Render(Load(fixture)));
        Assert.DoesNotContain("slots file:", OwnerFleetDiagram.Render(Load(fixture)));
    }

    [Theory]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(100)]
    public void SyncRowUsesConsoleZoneAndRespectsBoxWidth(int width)
    {
        using var fixture = new Harness();
        var row = Row("failed", Harness.At) with { Reason = "unreachable\ninjected" };
        var zone = TimeZoneInfo.CreateCustomTimeZone("fixture", TimeSpan.FromHours(-5), "fixture", "fixture");
        var report = RemoteExecutorReportReader.Read(fixture.Workspace, null, 10, Harness.At.AddHours(-24));
        var model = OwnerFleetViewModelBuilder.Build(report, false, zone,
            new Dictionary<string, RemoteExecutorSlotsSyncRow> { ["mcg-exec-1"] = row });
        Assert.Equal(TimeSpan.FromHours(-5), model.Executors[0].SlotsFile!.ObservedAt.Offset);
        var text = OwnerFleetDiagram.Render(model, width);
        Assert.All(text.Split('\n'), line => Assert.Equal(width, line.GetColumns()));
        Assert.DoesNotContain("\ninjected", text);
    }

    [Fact]
    public void LedgerWritesSnakeCaseFieldsAndKeepsExecutorIdentitiesDistinct()
    {
        using var fixture = new Harness();
        var row = Row("synced", Harness.At);
        RemoteExecutorSlotsSyncLedger.Append(Ledger(fixture), row);
        RemoteExecutorSlotsSyncLedger.Append(Ledger(fixture), row with { ExecutorId = "MCG-EXEC-1", Outcome = "failed" });
        using var json = JsonDocument.Parse(File.ReadAllLines(Ledger(fixture))[0]);
        Assert.Equal(new[] { "observed_at", "executor_id", "intent_id", "slots", "outcome", "reason", "remote_backup", "error_tail" },
            json.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(Harness.At, json.RootElement.GetProperty("observed_at").GetDateTimeOffset());
        Assert.Equal(2, RemoteExecutorSlotsSyncLedger.ReadLatestByExecutor(Ledger(fixture)).Count);
    }

    private static RemoteExecutorSlotsSyncRow Row(string outcome, DateTimeOffset at) =>
        new(at, "mcg-exec-1", "intent", 5, outcome, outcome == "failed" ? "unreachable" : null, null, null);
    private static string Ledger(Harness fixture) => Path.Combine(fixture.Folder, RemoteExecutorSlotsSyncLedger.FileName);
    private static OwnerFleetViewModel Load(Harness fixture) =>
        OwnerFleetViewModelBuilder.Load(fixture.Workspace, new OwnerConsoleHarness().Liveness, new ExecutorSettingIntentScenarioTests.FixedTime());
}
