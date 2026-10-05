using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: isolated root, per-test query probe, AsyncLocal console capture and fixed report window.
public sealed class CliRoundValueCascadeRouteTests : CliTaskQueryTestSupport
{
    [Fact]
    public void CascadeCohort_PrintsDecisionRowsBeforePendingAndAddsJsonRoutes()
    {
        var root = CreateTempDirectory();
        try
        {
            var at = DateTimeOffset.Parse("2026-10-05T12:00:00Z");
            TaskDispatchSnapshot Dispatch(int n, string? decision) => new("worker", "command", "root", at.AddMinutes(n),
                ModelSelectionReason: decision is null ? null : $"reason; cascade={decision} rule=tester-cheap-first");
            var tester = new TaskSnapshot("tester", "test", AgentRole.Tester, WorkTaskStatus.Completed,
                null, null, null, [], null, null, DispatchHistory: [Dispatch(1, "cheap"), Dispatch(2, "escalated"), Dispatch(3, "primary")]);
            var developer = new TaskSnapshot("developer", "build", AgentRole.Developer, WorkTaskStatus.Completed,
                null, null, null, [], null, null, DispatchHistory: [Dispatch(0, null)]);
            var snapshot = new GoalSnapshot("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "landed", GoalStatus.Completed,
                [tester, developer],
                [new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "tester", ProgressKind.TaskRetried,
                    "auto-review-retry round 2: corrected test evidence", at.AddMinutes(2).AddSeconds(30)),
                 new("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "tester", ProgressKind.TaskCompleted, "done", at.AddMinutes(4))]);
            var repository = new ProbeStateRepository(AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([snapshot], [])));
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            string[] args = ["round-value", "--since", "2026-10-05T00:00:00Z", "--until", "2026-10-06T00:00:00Z"];
            var table = CaptureConsole(() => CliRoundValueQueryCommand.Execute(args, repository, workspace));
            var lines = table.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
            var header = Array.IndexOf(lines, "Cascade route | Rounds | Productive | Overhead | Wasted");
            Assert.True(header >= 0);
            Assert.Equal("cheap | 1 | 0 | 0 | 1", lines[header + 1]);
            Assert.Equal("escalated | 1 | 0 | 0 | 1", lines[header + 2]);
            Assert.Equal("primary | 1 | 1 | 0 | 0", lines[header + 3]);
            Assert.Equal("Pending goals | 0 | rounds=0", lines[header + 4]);
            using var json = JsonDocument.Parse(CaptureConsole(() => CliRoundValueQueryCommand.Execute([.. args, "--json"], repository, workspace)));
            var routes = json.RootElement.GetProperty("cascadeRoutes").EnumerateArray().ToArray();
            Assert.Equal(["cheap", "escalated", "primary"], routes.Select(r => r.GetProperty("decision").GetString()));
            Assert.All(routes, r =>
            {
                Assert.Equal(1, r.GetProperty("rounds").GetInt32());
                Assert.Equal(1, r.GetProperty("productive").GetInt32() + r.GetProperty("overhead").GetInt32() + r.GetProperty("wasted").GetInt32());
            });
            Assert.Equal(0, repository.SaveAttempts);
            Assert.Equal(0, repository.MutationAttempts);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
