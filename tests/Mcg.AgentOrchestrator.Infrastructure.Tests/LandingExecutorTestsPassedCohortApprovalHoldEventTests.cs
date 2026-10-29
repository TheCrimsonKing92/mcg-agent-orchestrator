using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using static Mcg.AgentOrchestrator.Infrastructure.Tests.LandingExecutorTestsDenylistOwnerHold;

// Each fixture owns its git repository and stores; the collection isolates landing git hooks.
[Collection(TestCollections.LandingGitRunner)]
public sealed class LandingExecutorTestsPassedCohortApprovalHoldEventTests : AcceptanceCohortWorkflowTests
{
    [Fact]
    public void HeldTick_SecondMemberNeedsApproval_StreamsOneEventPerMember()
    {
        using var scenario = new PassedCohortReceiptApprovalHoldTests.Scenario(protectedMemberIndex: 1);
        var logPath = scenario.Workspace.ConductEventsLogPath;
        var progress = scenario.Tick(events: new ConductEventLogWriter(logPath));

        AssertHeldLines(scenario, progress);
        var events = ReadHeldEvents(logPath);
        Assert.Equal(2, events.Length);
        AssertHeldLines(scenario, events.Select(record => record.Detail).ToArray());
        Assert.All(scenario.Goals, goal => Assert.Single(events.Where(record =>
            record.GoalId == goal.Id.Value[..8])));
        Assert.Equal(1, scenario.Verifier.RunCount);
    }

    [Fact]
    [Trait("Category", "CrossTick")]
    public void TwoTicks_UnchangedHold_StreamsEachTickAndPersistsOneDecision()
    {
        using var scenario = new PassedCohortReceiptApprovalHoldTests.Scenario(protectedMemberIndex: 1);
        var persisted = new Dictionary<GoalId, int>();
        var ticks = new List<BatchTickSummary>();
        Assert.All(scenario.Goals, goal => Assert.Empty(HeldDecisions(goal)));

        // One Run preserves the solo-hold disposition cache across both ticks.
        new ConductorBatchLoop(conductEventLogWriter: new ConductEventLogWriter(
            scenario.Workspace.ConductEventsLogPath)).Run(
            scenario.Kernel, scenario.Driver, ConductorAutonomyPolicy.Permissive,
            Path.Combine(scenario.Repo, "stop-does-not-exist"), maxIterations: 2,
            watchInterval: TimeSpan.FromSeconds(1), sleepFunc: _ => false,
            persistGoalTick: (kernel, ids) =>
            {
                foreach (var id in ids)
                    persisted[id] = HeldDecisions(kernel.GetGoal(id)).Count;
            },
            onTick: tick =>
            {
                ticks.Add(tick);
                AssertHeldLines(scenario, tick.ProgressLines);
                Assert.All(scenario.Goals, goal =>
                {
                    var decision = Assert.Single(HeldDecisions(goal));
                    Assert.Contains(scenario.Receipt.Identity.Value, decision.Message, StringComparison.Ordinal);
                    // This check runs after each tick, including tick one, proving journal durability.
                    Assert.True(persisted.ContainsKey(goal.Id), "The held member journal was not persisted.");
                    Assert.Equal(1, persisted[goal.Id]);
                });
            });

        Assert.Equal(2, ticks.Count);
        var events = ReadHeldEvents(scenario.Workspace.ConductEventsLogPath);
        Assert.Equal(4, events.Length);
        Assert.All(scenario.Goals, goal => Assert.Equal(2,
            events.Count(record => record.GoalId == goal.Id.Value[..8])));
        Assert.Equal(1, scenario.Verifier.RunCount);
    }

    private static List<ProgressEvent> HeldDecisions(Goal goal) => goal.Timeline
        .Where(entry => entry.Kind == ProgressKind.GoalPolicyDecision &&
            entry.Message.Contains("held at Verified", StringComparison.Ordinal)).ToList();

    private static ConductEventRecord[] ReadHeldEvents(string path) => File.ReadAllLines(path)
        .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
        .Where(record => record.Detail.StartsWith("GOAL ", StringComparison.Ordinal) &&
            record.Detail.Contains("result=held", StringComparison.Ordinal)).ToArray();

    private static void AssertHeldLines(PassedCohortReceiptApprovalHoldTests.Scenario scenario,
        IReadOnlyList<string> progress)
    {
        var held = progress.Where(line => line.StartsWith("GOAL ", StringComparison.Ordinal) &&
            line.Contains("result=held", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, held.Length);
        var waiting = scenario.Goals[1].Id.Value[..8];
        foreach (var goal in scenario.Goals)
        {
            var line = Assert.Single(held.Where(line => line.StartsWith(
                $"GOAL goal={goal.Id.Value[..8]} ", StringComparison.Ordinal)));
            Assert.Contains("result=held state=Verified", line, StringComparison.Ordinal);
            Assert.Contains("landing-denylist_owner_approval_required", line, StringComparison.Ordinal);
            Assert.Contains($"cohort={scenario.Receipt.Identity.Value}", line, StringComparison.Ordinal);
            Assert.Contains($"waiting={waiting}", line, StringComparison.Ordinal);
            Assert.Contains($"candidate={scenario.CandidateRevisions[1]}", line, StringComparison.Ordinal);
            Assert.Contains("rule=landing-policy", line, StringComparison.Ordinal);
            Assert.Contains($"path={Protected}", line, StringComparison.Ordinal);
            Assert.Contains($"approve-policy-change_{waiting}_{scenario.CandidateRevisions[1]}",
                line, StringComparison.Ordinal);
        }
    }
}
