using Mcg.AgentOrchestrator.App.OwnerConsole;
using static OwnerFleetFullScreenWidthScenarioTests;

// Unique filesystem roots, no terminal/global state; safe to run in parallel.
public sealed class OwnerFleetBlankExecutorTests
{
    [Fact]
    public void LedgerSkipBelongsToHostWithoutPhantomExecutor()
    {
        using var fixture = new FleetFixture();
        var model = OwnerFleetViewModelBuilder.Build(fixture.Read(), true, TimeZoneInfo.Utc);
        Assert.Equal(2, model.Executors.Count);
        Assert.Equal(new[] { "mcg-exec-1", "mcg-exec-2" }, model.Executors.Select(node => node.Id));
        Assert.Equal(1, model.Host.NoExecutorFreeCount);
        var text = OwnerFleetDiagram.Render(model);
        Assert.Contains("executors: 2", text);
        Assert.Contains("no executor free: 1 (last 2026-10-17", text);
        Assert.Equal(3, text.Count(ch => ch is '┌' or '╔'));
        Assert.DoesNotContain(text.Split('\n'), line => line.Trim('│', '║', ' ') == "Executor");
    }

    [Fact]
    public void SkipTimeUsesConsoleZone()
    {
        using var fixture = new FleetFixture();
        var zone = TimeZoneInfo.CreateCustomTimeZone("fixture", TimeSpan.FromHours(-5), "fixture", "fixture");
        var model = OwnerFleetViewModelBuilder.Build(fixture.Read(), true, zone);
        Assert.Equal(TimeSpan.FromHours(-5), model.Host.NoExecutorFreeLastAt!.Value.Offset);
        Assert.Contains("last 2026-10-17 07:01 -05:00", OwnerFleetDiagram.Render(model));
    }

    [Fact]
    public void NoSkipsOmitsHostSummary()
    {
        using var fixture = new FleetFixture(skipped: false);
        var model = OwnerFleetViewModelBuilder.Build(fixture.Read(), true, TimeZoneInfo.Utc);
        Assert.Equal(0, model.Host.NoExecutorFreeCount);
        Assert.Null(model.Host.NoExecutorFreeLastAt);
        Assert.DoesNotContain("no executor free", OwnerFleetDiagram.Render(model));
    }
}
