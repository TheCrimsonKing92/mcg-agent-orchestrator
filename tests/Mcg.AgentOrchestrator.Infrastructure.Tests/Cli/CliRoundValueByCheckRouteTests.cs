using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: isolated roots, snapshot-only state, AsyncLocal console capture.
public sealed class CliRoundValueByCheckRouteTests : CliTaskQueryTestSupport
{
    private static readonly string[] WindowArgs =
        ["round-value", "--since", "2026-09-24T00:00:00Z", "--until", "2026-09-26T00:00:00Z"];

    [Fact]
    public void ByCheckPrintsPerKeyCountsAndUnattributedRowWithoutWrites()
    {
        WithRepository((repository, root) =>
        {
            var text = Run(repository, root, [.. WindowArgs, "--by-check"]);
            Assert.Contains("Round value by check [", text);
            Assert.Contains("Check | Rounds | Productive | Overhead | Wasted | Goals landed | Goals lost | Outcome classes", text);
            Assert.Contains("source-size-ratchet:src/A.cs | 2 | 1 | 0 | 1 | 1 | 1 | success=2", text);
            Assert.Contains("unattributed | 1 | 0 | 0 | 1 | 1 | 0 | none", text);
        });
    }

    [Fact]
    public void ByCheckJsonUsesSliceRowsAndRecordedOutcomeClassesWithoutWrites()
    {
        WithRepository((repository, root) =>
        {
            using var json = JsonDocument.Parse(Run(repository, root, [.. WindowArgs, "--by-check", "--json"]));
            var rows = json.RootElement.GetProperty("rows").EnumerateArray().ToArray();
            Assert.Equal(2, rows.Length);
            var check = rows[0];
            Assert.Equal("source-size-ratchet:src/A.cs", check.GetProperty("check").GetString());
            Assert.Equal(2, check.GetProperty("rounds").GetInt32());
            Assert.Equal(1, check.GetProperty("productive").GetInt32());
            Assert.Equal(0, check.GetProperty("overhead").GetInt32());
            Assert.Equal(1, check.GetProperty("wasted").GetInt32());
            Assert.Equal(1, check.GetProperty("goalsLanded").GetInt32());
            Assert.Equal(1, check.GetProperty("goalsLost").GetInt32());
            var outcome = Assert.Single(check.GetProperty("outcomeClasses").EnumerateArray());
            Assert.Equal("success", outcome.GetProperty("outcomeClass").GetString());
            Assert.Equal(2, outcome.GetProperty("rounds").GetInt32());
            Assert.Equal("unattributed", rows[1].GetProperty("check").GetString());
            Assert.Equal(1, rows[1].GetProperty("rounds").GetInt32());
            Assert.Empty(rows[1].GetProperty("outcomeClasses").EnumerateArray());
        });
    }

    [Fact]
    public void BothFlagsReportBothSlicesAndKeepSkillOnlyJsonShape()
    {
        WithRepository((repository, root) =>
        {
            var text = Run(repository, root, [.. WindowArgs, "--by-skill", "--by-check"]);
            Assert.Contains("Round value by skill [", text);
            Assert.Contains("Round value by check [", text);
            using var both = JsonDocument.Parse(Run(repository, root, [.. WindowArgs, "--by-skill", "--by-check", "--json"]));
            var skill = both.RootElement.GetProperty("bySkill");
            Assert.Contains(skill.GetProperty("rows").EnumerateArray(),
                row => row.GetProperty("skill").GetString() == "verification-before-completion");
            Assert.Equal("source-size-ratchet:src/A.cs",
                both.RootElement.GetProperty("byCheck").GetProperty("rows")[0].GetProperty("check").GetString());
            using var alone = JsonDocument.Parse(Run(repository, root, [.. WindowArgs, "--by-skill", "--json"]));
            Assert.Equal(alone.RootElement.GetRawText(), skill.GetRawText());
            Assert.True(alone.RootElement.TryGetProperty("rows", out _));
            Assert.Contains("[--by-check]", CliCommandHelp.RoundValueUsage);
        });
    }

    private static void WithRepository(Action<ProbeStateRepository, string> action)
    {
        var root = CreateTempDirectory();
        try
        {
            var repository = new ProbeStateRepository(Create()) { ThrowOnOutbox = true };
            action(repository, root);
            Assert.Equal(0, repository.SaveAttempts);
            Assert.Equal(0, repository.MutationAttempts);
            Assert.Equal(0, repository.FullLoadAttempts);
            Assert.Equal(0, repository.ListOutboxMessagesCount);
            Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string Run(ProbeStateRepository repository, string root, string[] args) => CaptureConsole(() =>
    {
        Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(args));
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? goal = null;
        Assert.True(CliReadOnlyCommandRunner.TryExecute(args, repository, OrchestratorWorkspace.ForDirectory(root),
            new InMemoryModelProviderRegistry([]), null, ref agents, ref profiles, ref goal, out var changed));
        Assert.False(changed);
    });

    private static AgentOrchestratorKernel Create()
    {
        var at = DateTimeOffset.Parse("2026-09-24T01:00:00Z", CultureInfo.InvariantCulture);
        TaskSnapshot Task(string id) => new(id, id, AgentRole.Developer, WorkTaskStatus.Completed,
            null, null, null, [], null, null, DispatchHistory:
                [new("worker", "command", "root", at), new("worker", "command", "root", at.AddHours(1))]);
        GoalSnapshot Goal(string id, GoalStatus status, bool orphan)
        {
            var events = new List<ProgressEventSnapshot>
            {
                new(id, "dev", ProgressKind.TaskCompleted, "done", at.AddMinutes(10)),
                new(id, "dev", ProgressKind.TaskRetried,
                    "developer-completion structural pre-check failed: src/A.cs has 700 lines, exceeding the recorded ceiling of 650.", at.AddMinutes(50)),
                new(id, "dev", ProgressKind.TaskCompleted, "done", at.AddHours(1).AddMinutes(10)),
                new(id, "dev", ProgressKind.TaskNote, "CLASSIFIER rule=x; outcome_class=success", at.AddHours(1).AddMinutes(11)),
                new(id, "dev", ProgressKind.TaskNote,
                    "SKILLS selected=verification-before-completion read=verification-before-completion claimed=verification-before-completion", at.AddHours(1).AddMinutes(12))
            };
            var tasks = new List<TaskSnapshot> { Task("dev") };
            if (orphan)
            {
                tasks.Add(Task("orphan"));
                events.Add(new(id, "orphan", ProgressKind.TaskCompleted, "done", at.AddHours(1).AddMinutes(10)));
            }
            return new(id, "Checks", status, tasks, events);
        }
        return AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [Goal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", GoalStatus.Completed, true),
             Goal("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", GoalStatus.Cancelled, false)], []));
    }
}
