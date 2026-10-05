using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: isolated roots, snapshot-only state, AsyncLocal console capture.
public sealed class CliRoundValueBySkillRouteTests : CliTaskQueryTestSupport
{
    private static readonly string[] WindowArgs =
        ["round-value", "--since", "2026-09-24T00:00:00Z", "--until", "2026-09-26T00:00:00Z"];

    [Fact]
    public void BySkillPrintsObservedCountsAndUnrecordedRowsWithoutWrites()
    {
        var root = CreateTempDirectory();
        try
        {
            var repository = new ProbeStateRepository(Create(includeNotes: true)) { ThrowOnOutbox = true };
            var text = Run(repository, root, [.. WindowArgs, "--by-skill"]);
            Assert.Contains("Skill | Selected | Read | Claimed | Productive | Overhead | Wasted", text);
            Assert.Contains("verification-before-completion | 2 | 1 | 1 | 1 | 1 | 0", text);
            Assert.Contains("unrecorded | 0 | 0 | 0 | 0 | 0 | 1", text);
            Assert.Contains("Read unavailable | 0", text);
            using var json = JsonDocument.Parse(Run(repository, root, [.. WindowArgs, "--by-skill", "--json"]));
            var rows = json.RootElement.GetProperty("rows").EnumerateArray().ToArray();
            Assert.Equal(2, rows.Length);
            Assert.Equal("verification-before-completion", rows[0].GetProperty("skill").GetString());
            Assert.Equal(2, rows[0].GetProperty("selected").GetInt32());
            Assert.Equal(1, rows[0].GetProperty("read").GetInt32());
            Assert.Equal(1, rows[0].GetProperty("claimed").GetInt32());
            Assert.Equal("unrecorded", rows[1].GetProperty("skill").GetString());
            Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify([.. WindowArgs, "--by-skill"]));
            Assert.Equal(0, repository.SaveAttempts);
            Assert.Equal(0, repository.MutationAttempts);
            Assert.Equal(0, repository.FullLoadAttempts);
            Assert.Equal(0, repository.ListOutboxMessagesCount);
            Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultReportRemainsByteIdenticalWithAndWithoutSkillNotes(bool json)
    {
        var root = CreateTempDirectory();
        try
        {
            var args = json ? [.. WindowArgs, "--json"] : WindowArgs;
            var baseline = Run(new ProbeStateRepository(Create(false)), root, args);
            Assert.Equal(baseline, Run(new ProbeStateRepository(Create(true)), root, args));
            if (!json)
                Assert.Equal(string.Join(Environment.NewLine,
                [
                    "Round value [2026-09-24T00:00:00.0000000+00:00, 2026-09-26T00:00:00.0000000+00:00) | cohort = landed or lost goals whose last round is in the window",
                    "Day | Landed | Lost | Rounds | Productive | Overhead | Wasted | Rounds per landing | Waste share | Input | Cached input | Output | Usage unreported",
                    "2026-09-24 | 1 | 0 | 3 | 1 | 1 | 1 | 3 | 0.333 | 0 | 0 | 0 | 3",
                    "Window | 1 | 0 | 3 | 1 | 1 | 1 | 3 | 0.333 | 0 | 0 | 0 | 3",
                    "Waste cause | Rounds | Share",
                    "orphaned-dispatch | 1 | 0.333",
                    "Pending goals | 0 | rounds=0",
                    ""
                ]), baseline);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string Run(ProbeStateRepository repository, string root, string[] args) => CaptureConsole(() =>
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? goal = null;
        Assert.True(CliReadOnlyCommandRunner.TryExecute(args, repository, OrchestratorWorkspace.ForDirectory(root),
            new InMemoryModelProviderRegistry([]), null, ref agents, ref profiles, ref goal, out var changed));
        Assert.False(changed);
    });

    private static AgentOrchestratorKernel Create(bool includeNotes)
    {
        const string goal = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var at = DateTimeOffset.Parse("2026-09-24T01:00:00Z");
        TaskSnapshot Task(string id, AgentRole role) => new(id, id, role, WorkTaskStatus.Completed,
            null, null, null, [], null, null, DispatchHistory: [new("worker", "command", "root", at)]);
        ProgressEventSnapshot Event(string task, ProgressKind kind, string message) => new(goal, task, kind, message, at.AddMinutes(1));
        var timeline = new List<ProgressEventSnapshot>
        {
            Event("dev", ProgressKind.TaskCompleted, "done"), Event("review", ProgressKind.TaskCompleted, "done")
        };
        if (includeNotes)
        {
            timeline.Add(Event("dev", ProgressKind.TaskNote, "SKILLS goal=aaaaaaaa task=dev selected=verification-before-completion read=verification-before-completion claimed=verification-before-completion"));
            timeline.Add(Event("review", ProgressKind.TaskNote, "SKILLS goal=aaaaaaaa task=review selected=verification-before-completion read=none claimed=none"));
        }
        return AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([
            new GoalSnapshot(goal, "Skills", GoalStatus.Completed,
                [Task("dev", AgentRole.Developer), Task("review", AgentRole.Reviewer), Task("orphan", AgentRole.Tester)], timeline)], []));
    }
}
