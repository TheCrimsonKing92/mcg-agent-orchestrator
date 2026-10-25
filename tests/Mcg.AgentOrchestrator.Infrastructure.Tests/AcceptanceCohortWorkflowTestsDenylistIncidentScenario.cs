using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using static Mcg.AgentOrchestrator.Infrastructure.Tests.LandingExecutorTestsDenylistOwnerHold;
using static Mcg.AgentOrchestrator.Infrastructure.Tests.LandingExecutorTestsLandingDenylist;

[Collection(TestCollections.LandingGitRunner)]
public sealed class AcceptanceCohortWorkflowTestsDenylistIncidentScenario : AcceptanceCohortWorkflowTests
{
    [Fact]
    public void MergeTrain_PolicyIncident_HoldsThenLandsExactTrainAfterOwnerApproval()
    {
        var repo = CreateAcceptanceCohortRepository();
        try
        {
            using var scenario = AcceptanceCohortWorkflowTestsDenylistOwnerHold.CreateScenario(
                repo, train: true, firstPath: Protected, seedPolicy: true);
            var writer = new GoalLifecycleEventWriter(scenario.Workspace.GoalLifecycleEventsDirectory);
            var held = scenario.Land(writer);
            Assert.Equal(AcceptanceCohortLandingOutcome.RetryableHold, held.Outcome);
            Assert.Equal(scenario.Main, RunGitOutput(repo, "rev-parse", "main").Trim());
            Assert.All(scenario.Goals, goal => AssertNoLanded(scenario.Workspace, goal.Id));
            var item = Assert.Single(CollaborationItemStore.ForDirectory(scenario.Workspace.OrchestratorDirectory)
                .GetAttentionQueueAsync().GetAwaiter().GetResult());
            Assert.Equal(scenario.Goals[0].Id.Value, item.GoalId);
            Assert.Contains("rule=landing-policy", item.Body, StringComparison.Ordinal);
            Assert.Contains(Protected, item.Body, StringComparison.Ordinal);
            Approve(scenario.Workspace, scenario.Goals[0], scenario.CandidateRevisions[0]);
            Assert.Equal(AcceptanceCohortLandingOutcome.Advanced, scenario.Land(writer).Outcome);
            Assert.Equal(scenario.Commit, RunGitOutput(repo, "rev-parse", "main").Trim());
            using var line = ReadLanded(scenario.Workspace, scenario.Goals[0].Id);
            Assert.Equal("denylist-match-owner-approved", line.RootElement.GetProperty("admissionRule").GetString());
            Assert.Equal([$"landing-policy={Protected}"], ReadMatches(line));
        }
        finally { DeleteDirectory(repo); }
    }
}
