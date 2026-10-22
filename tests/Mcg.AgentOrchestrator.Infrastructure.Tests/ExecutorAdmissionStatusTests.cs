using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fixture-owned configuration and ledgers, no network calls.
public sealed class ExecutorAdmissionStatusTests
{
    [Fact]
    public void Status_DisabledAndHistorical_ShowsAdmissionSeparatelyFromHealth()
    {
        using var fixture = new ExecutorSettingIntentScenarioTests.Harness();
        fixture.Write(ExecutorSettingIntentScenarioTests.Harness.Json.Replace("\"slots\": 3", "\"enabled\": false, \"slots\": 3"));
        var binding = new RemoteLaneBinding("historic", "lane", "commit", "tree", "main", "filter", "manifest");
        RemoteExecutorHealthLedger.Append(RemoteExecutorHealthLedger.ResolveStorePath(fixture.Workspace.RootDirectory),
            new(ExecutorSettingIntentScenarioTests.Harness.At, "historic", "attempt", "lane", RemoteLaneOutcomeCode.Accepted, null, binding, binding));
        var report = RemoteExecutorReportReader.Read(fixture.Workspace, null, 10);
        Assert.Equal(3, report.Executors.Count);
        Assert.False(report.Executors.Single(e => e.ExecutorId == "mcg-exec-1").AdmissionEnabled);
        Assert.True(report.Executors.Single(e => e.ExecutorId == "mcg-exec-2").AdmissionEnabled);
        Assert.Null(report.Executors.Single(e => e.ExecutorId == "historic").AdmissionEnabled);
        Assert.All(report.Executors, row => Assert.Null(row.LastProbe));
        var model = OwnerFleetViewModelBuilder.Build(report, true, TimeZoneInfo.Utc);
        var diagram = OwnerFleetDiagram.Render(model);
        Assert.Contains("admission: disabled", diagram);
        Assert.Contains("admission: enabled", diagram);
        Assert.Contains("admission: not configured", diagram);
        var json = AsyncLocalConsoleRouter.Capture(() => CliRemoteExecutorsQueryCommand.Execute(["remote-executors", "--json"], fixture.Workspace));
        Assert.Contains("\"admissionEnabled\":false", json);
        var text = AsyncLocalConsoleRouter.Capture(() => CliRemoteExecutorsQueryCommand.Execute(["remote-executors"], fixture.Workspace));
        Assert.Contains("admission\tmcg-exec-1\tdisabled", text);
        Assert.Contains("admission\thistoric\tnot-configured", text);
    }
}
