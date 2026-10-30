using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.Infrastructure.Tests.LandingExecutorTestsDenylistOwnerHold;
using static Mcg.AgentOrchestrator.Infrastructure.Tests.LandingExecutorTestsLandingDenylist;

// Each fixture owns its git repository and stores; the collection isolates landing git hooks.
[Collection(TestCollections.LandingGitRunner)]
public sealed class LandingExecutorTestsPassedCohortApprovedLandingNegativeControlTests : AcceptanceCohortWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadyTick_NoMissingApprovals_LandsBothWithoutHeldEvents(bool protectedButApproved)
    {
        using var scenario = new PassedCohortReceiptApprovalHoldTests.Scenario(
            protectedMemberIndex: protectedButApproved ? 1 : null);
        var decisions = CollaborationItemStore.ForDirectory(scenario.Workspace.OrchestratorDirectory);
        if (protectedButApproved)
            Approve(scenario.Workspace, scenario.Goals[1], scenario.CandidateRevisions[1]);
        Assert.Empty(decisions.GetAttentionQueueAsync().GetAwaiter().GetResult());

        // The receipt must be selectable by production admission, including its resource rules.
        var candidates = scenario.Goals.Select(goal => new ConductorSpeculativeAcceptanceCandidate(
            goal.Id, scenario.Driver.ProjectGroupedAdmissionCandidate(
                goal, ConductorAutonomyPolicy.Permissive, scenario.Kernel))).ToArray();
        var selection = Assert.Single(scenario.Driver.FindLandablePassedCohortSelections(candidates));
        Assert.Equal(scenario.Receipt.Identity.Value,
            scenario.Driver.ReadCurrentPassedCohortReceipt(selection)!.Identity.Value);

        var progress = scenario.Tick(events: new ConductEventLogWriter(scenario.Workspace.ConductEventsLogPath));

        Assert.Contains(progress, line => line.Contains(
            $"prelanded=true outcome=passed receipt={scenario.Receipt.ReceiptId}", StringComparison.Ordinal));
        Assert.DoesNotContain(progress, line => line.Contains("result=held", StringComparison.Ordinal) ||
            line.Contains("landing-denylist_owner_approval_required", StringComparison.Ordinal) ||
            line.Contains("PassedCohortReceiptApprovalHeld", StringComparison.Ordinal));
        var events = File.ReadAllLines(scenario.Workspace.ConductEventsLogPath)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))!).ToArray();
        Assert.NotEmpty(events);
        Assert.DoesNotContain(events, record => record.Detail.StartsWith("GOAL ", StringComparison.Ordinal) &&
            record.Detail.Contains("result=held", StringComparison.Ordinal));
        var attention = decisions.GetAttentionQueueAsync().GetAwaiter().GetResult();
        Assert.DoesNotContain(attention, item => item.Type == CollaborationItemType.Decision);
        Assert.Empty(CliCommandHandlers.CollaborationItemsForAttention(attention, scenario.Kernel,
            null, includeHistory: false));
        Assert.Equal(1, scenario.Verifier.RunCount);
        Assert.All(scenario.Goals, goal =>
        {
            Assert.Equal(GoalStatus.Completed, goal.Status);
            Assert.Equal(GoalStatus.Completed, scenario.PersistedStatuses[goal.Id]);
            Assert.Equal(scenario.Receipt.ReceiptId,
                Assert.Single(scenario.Store.ReadFinalizedLandingsForGoal(goal.Id)).ReceiptId);
            using var landed = ReadLanded(scenario.Workspace, goal.Id);
            Assert.Equal("GoalLanded", landed.RootElement.GetProperty("eventType").GetString());
        });
    }
}
