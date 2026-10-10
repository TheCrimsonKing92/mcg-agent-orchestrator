using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: pure event inputs and per-test state with an explicit clock.
public sealed class OwnerExperimentReadingActivityTests
{
    private const string ExperimentId = "1a2b3c4d0123456789abcdef01234567";
    private const string ExperimentPrefix = "1a2b3c4d";

    [Fact]
    public void BothTriggersNameTheExperimentCountAndResultCommandsWithoutInternalTerms()
    {
        Assert.Equal(32, ExperimentId.Length);
        var stopRule = ReadingDue("stop-rule", "10");
        var guardrail = ReadingDue("guardrail", "unavailable");
        Assert.True(OwnerActivityNarrator.Maps(stopRule));
        Assert.True(OwnerActivityNarrator.Maps(guardrail));
        var stopItem = Assert.Single(OwnerActivityNarrator.Narrate([stopRule], _ => ""));
        var guardrailItem = Assert.Single(OwnerActivityNarrator.Narrate([guardrail], _ => ""));

        Assert.Equal($"Experiment {ExperimentPrefix} reading due: stop rule reached", stopItem.Phrase);
        Assert.Equal($"Experiment {ExperimentPrefix} reading due: guardrail breached", guardrailItem.Phrase);
        Assert.NotEqual(stopItem.Phrase, guardrailItem.Phrase);
        Assert.Equal("The observed count is 10.", stopItem.Why);
        Assert.Equal("The observed count is not available.", guardrailItem.Why);
        foreach (var item in new[] { stopItem, guardrailItem })
        {
            Assert.Equal("", item.GoalPrefix);
            foreach (var text in new[] { item.Act, item.Next })
            {
                Assert.Contains($"experiment-show {ExperimentPrefix}", text);
                Assert.Contains($"experiment-decide {ExperimentPrefix}", text);
                Assert.Contains("record the result", text);
            }
            foreach (var text in new[] { item.Phrase, item.Why, item.Next, item.Act, OwnerActivityNarrator.Explain(item) })
                foreach (var forbidden in new[] { "EXPERIMENT_READING_DUE", "stopRuleMet", "guardrailBreached", ExperimentId,
                    "none", "child result", "handoff", "cohort", "receipt", "canary", "tick", "eventKind" })
                    Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task LiveFoldAndStartupFilterKeepGoalLessReadingDueBesideGoalActivity()
    {
        var harness = new OwnerConsoleHarness();
        var goal = harness.AddGoal("11111111", "# Search", AgentRole.Developer);
        var now = harness.Clock.GetUtcNow();
        var readingDue = ReadingDue("stop-rule", "10") with { Timestamp = now.AddSeconds(-2) };
        var stalled = new OwnerConductEvent(now.AddSeconds(-1), "goal-stalled", goal.Id.Value,
            "GOAL_STALLED repeatedForSeconds=120 blocker=waiting_for_approval");
        var builder = new OwnerConsoleViewModelBuilder(harness.State, harness.Questions, harness.Liveness,
            new Epics(), harness.Clock);

        var model = await builder.BuildAsync(new(now, null, [readingDue, stalled], 0));

        Assert.Equal(2, model.Activity.Length);
        var readingRow = Assert.Single(model.Activity, item => item.Kind == "experiment-reading-due");
        Assert.Equal("", readingRow.GoalPrefix);
        Assert.Equal($"Experiment {ExperimentPrefix} reading due: stop rule reached", readingRow.Phrase);
        Assert.EndsWith(readingRow.Phrase, OwnerActivityNarrator.Line(readingRow));
        Assert.Equal("11111111", Assert.Single(model.Activity, item => item.Kind == "goal-stalled").GoalPrefix);
        Assert.True(OwnerConsoleStartupActivity.IsOperatorEvent(readingDue));
        Assert.False(OwnerConsoleStartupActivity.IsOperatorEvent(new(now, "goal-lifecycle", goal.Id.Value, "TaskCreated")));
    }

    [Theory]
    [InlineData("EXPERIMENT_READING_DUE trigger=stop-rule observed=10")]
    [InlineData("EXPERIMENT_READING_DUE experiment= trigger=stop-rule observed=10")]
    public void MissingOrEmptyExperimentProducesNoActivity(string detail)
    {
        var input = new OwnerConductEvent(DateTimeOffset.UnixEpoch, "experiment-reading-due", null, detail);
        Assert.True(OwnerActivityNarrator.Maps(input));
        Assert.Empty(OwnerActivityNarrator.Narrate([input], _ => ""));
    }

    [Theory]
    [InlineData("experiment=123 trigger=unknown observed=invalid")]
    [InlineData("experiment=123 observed=unavailable")]
    public void ShortExperimentAndUnknownTriggerRemainVisible(string fields)
    {
        var input = new OwnerConductEvent(DateTimeOffset.UnixEpoch, "experiment-reading-due", null,
            "EXPERIMENT_READING_DUE " + fields);
        var item = Assert.Single(OwnerActivityNarrator.Narrate([input], _ => ""));
        Assert.Equal("Experiment 123 reading due", item.Phrase);
        Assert.Equal("The observed count is not available.", item.Why);
        Assert.Contains("experiment-show 123", item.Act);
        Assert.Contains("experiment-decide 123", item.Act);
    }

    private static OwnerConductEvent ReadingDue(string trigger, string observed) => new(DateTimeOffset.UnixEpoch,
        "experiment-reading-due", null, $"EXPERIMENT_READING_DUE experiment={ExperimentId} trigger={trigger} observed={observed} " +
        "stopRuleMet=true verdict=none child result handoff cohort receipt canary tick eventKind guardrailBreached=false");

    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string goalId, CancellationToken token) => Task.FromResult("");
    }
}
