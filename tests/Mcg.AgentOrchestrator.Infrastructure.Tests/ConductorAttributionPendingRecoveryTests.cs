using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;
using Fixture = ConductorDriverTestsAttributionPending.Fixture;

public sealed class ConductorAttributionPendingRecoveryTests : GoalWorktreeTestBase
{
    [Theory]
    [InlineData("introduced")]
    [InlineData("inherited")]
    [InlineData("repaired")]
    public async Task ConfirmedOperatorRetryClearsPersistentHoldAndReevaluatesNewEvidence(string nextEvidence)
    {
        var repo = CreateSeededRepository();
        try
        {
            using var fixture = new Fixture();
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
            var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
            var initial = MakeDriver(runAcceptanceSummary: _ => fixture.Summary,
                recordAcceptanceFailure: (goal, checks, branch, main, attrs, baseline) =>
                    fixture.Kernel.RecordAcceptanceFailure(goal.Id, checks, branch, main, attrs, baseline),
                getLandingFileScopes: _ => ["src/Changed.cs"], apparatusRedGate: fixture.Gate,
                writeEscalationWithResult: (goal, _, reason) => OperatorInbox.RecordLandingEscalation(workspace, goal, reason, "conductor:acceptance", collaborationStore: store));
            var held = ConductorDriverTestsAttributionPending.AssertPending(initial.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Conservative), fixture.Goal);
            var attention = Assert.Single(await store.ListAsync(fixture.Goal.Id.Value));
            Assert.Equal(CollaborationItemStatus.Raised, attention.Status);
            await repository.SaveAsync(fixture.Kernel);
            var kernel = await repository.LoadAsync();
            var goal = kernel.GetGoal(fixture.Goal.Id);
            Assert.Equal(held.StableIdentity, goal.LatestAcceptanceFailure!.AttributionPending!.Identity);
            var occurrence = goal.LatestAcceptanceFailure.OccurredAt;
            var context = CreateAcceptanceContext(kernel, repo, goal);
            Assert.Throws<ArgumentException>(() => CliCommandHandlers.Execute(
                ["acceptance-retry", goal.Id.Value[..8], "verified repair supplied"], context));
            Assert.Equal(GoalStatus.AcceptanceFailed, goal.Status);
            Assert.Equal(occurrence, goal.LatestAcceptanceFailure!.OccurredAt);
            Assert.Equal(OperatorInbox.LandingEscalationResolution.Superseded,
                OperatorInbox.ResolveLandingEscalationOccurrence(workspace, goal, "stale retry", occurrence.AddTicks(-1)));
            Assert.Equal(CollaborationItemStatus.Raised, Assert.Single(await store.ListAsync(goal.Id.Value)).Status);
            CliCommandHandlers.Execute(["acceptance-retry", goal.Id.Value[..8], "verified repair supplied", "--confirm-acceptance-retry"], context);
            Assert.Equal(GoalStatus.Verified, goal.Status);
            Assert.Null(goal.RetainedAcceptanceFailure);
            Assert.Equal(1, goal.OperatorAcceptanceRegateCount);
            Assert.Equal(0, goal.AutomaticAcceptanceRetryCount);
            var resolved = Assert.Single(await store.ListAsync(goal.Id.Value));
            Assert.Equal(attention.Id, resolved.Id);
            Assert.Equal(CollaborationItemStatus.Resolved, resolved.Status);
            Assert.Equal("verified repair supplied", resolved.Resolution);
            Assert.Equal(OperatorInbox.LandingEscalationResolution.AlreadyResolved,
                OperatorInbox.ResolveLandingEscalationOccurrence(workspace, goal, "audit replay", occurrence));
            var replayed = Assert.Single(await store.ListAsync(goal.Id.Value));
            Assert.Equal(resolved.Id, replayed.Id);
            Assert.Equal(resolved.Status, replayed.Status);
            Assert.Equal(resolved.Resolution, replayed.Resolution);
            Assert.Equal(resolved.ResolvedAt, replayed.ResolvedAt);
            await repository.SaveAsync(kernel);
            var cleared = (await repository.LoadAsync()).GetGoal(goal.Id);
            Assert.Null(cleared.RetainedAcceptanceFailure);

            var attributions = Fixture.Identities.Select(identity => new AcceptanceTestFailureAttribution(identity,
                nextEvidence == "inherited" ? AcceptanceTestFailureOrigin.Inherited : AcceptanceTestFailureOrigin.Introduced,
                nextEvidence == "inherited" ? "executed baseline failed for exact identity; receipt baseline.trx" : "executed baseline passed for exact identity; receipt baseline.trx")).ToArray();
            fixture.Check = fixture.Check with { FailingTestAttributions = attributions };
            var gates = 0;
            var developerRetries = 0;
            var next = MakeDriver(runAcceptanceSummary: _ =>
                {
                    gates++;
                    return nextEvidence == "repaired" ? AcceptanceVerificationSummary.PassedWithNoUnmetCriteria : fixture.Summary;
                },
                retryTaskWithCause: (id, task, message, round, cause) =>
                { developerRetries++; return kernel.RetryTask(id, task, message, retryRoundKind: round, retryCause: cause); },
                recordCriterionRetryFeedback: kernel.RecordCriterionRetryFeedback,
                recordAcceptanceFailure: (g, checks, branch, main, attrs, baseline) => kernel.RecordAcceptanceFailure(g.Id, checks, branch, main, attrs, baseline),
                getLandingFileScopes: _ => ["src/Changed.cs"], apparatusRedGate: fixture.Gate);
            var result = next.AdvanceOnce(goal, ConductorAutonomyPolicy.Conservative);
            Assert.Equal(1, gates);
            Assert.Equal(nextEvidence == "introduced" ? 1 : 0, developerRetries);
            Assert.Null(goal.LatestAcceptanceFailure?.AttributionPending);
            if (nextEvidence == "inherited")
            {
                var inherited = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
                Assert.Equal(GoalLifecycleState.Verified, inherited.State);
                Assert.Equal("excluded-unattributable-hold", inherited.Decision!.Facts.Single(fact => fact.Name == "disposition").Value);
            }
            else Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
            Assert.Single(await store.ListAsync(goal.Id.Value));
        }
        finally { DeleteDirectory(repo); }
    }
}
