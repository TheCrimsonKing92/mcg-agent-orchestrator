using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using static ConductorDriverTests;

[Xunit.Collection("IsolatedProcessSpawning")]
public sealed class ConductorDriverTestsGatePassedLifecycle
{
    [Xunit.Fact]
    public void GatePassedWithoutLanding_RemainsVerifiedAndGateReady()
    {
        const string branchRevision = "1111111111111111111111111111111111111111";
        const string mainRevision = "2222222222222222222222222222222222222222";
        var root = Path.Combine(Path.GetTempPath(), "mcg-gate-passed-lifecycle", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var (kernel, goal) = SimpleGoal("Gate-passed lifecycle");
            PassVerification(kernel, goal, goal.Tasks.Single());
            Assert.Equal(GoalLifecycleState.Verified, GoalLifecycle.ResolveState(goal));
            GoalOperationJournal.AcceptanceGatePassed(
                root, goal, "acceptance", branchRevision, mainRevision, "Gate passed; merge pending");
            var journal = GoalOperationJournal.Read(root, goal.Id);
            var entry = Assert.Single(journal.Entries);
            Assert.Equal("acceptance", entry.Operation);
            Assert.Equal(GoalOperationStatus.Completed, entry.Status);
            Assert.Equal("gate-passed", entry.AcceptanceOutcome);

            GoalLifecycleFacts ReadJournalFacts(Goal candidate)
            {
                var current = GoalOperationJournal.Read(root, candidate.Id);
                return new GoalLifecycleFacts(
                    IsMerged: GoalOperationJournal.HasCompletedLandingEvidence(current),
                    IsRecorded: GoalOperationJournal.HasCompletedRecordEvidence(current));
            }

            var projector = new GateReadyCandidateProjector(
                _ => new GateReadyCandidateRevisionPair(branchRevision, mainRevision),
                _ => new GateReadyLandingScopeObservation(
                    Succeeded: true, Files: ["src/Mcg.AgentOrchestrator.Core/Feature.cs"]),
                (_, _, _) => new GateReadyMergeTreeObservation(IsClean: true));
            var driver = MakeDriver(
                getFacts: ReadJournalFacts,
                classifyRisk: _ => ChangeRiskTier.DocsOnly,
                isVerificationGateSatisfied: _ => true,
                gateReadyCandidateProjector: projector);

            Assert.Equal(GoalLifecycleState.Verified, GoalLifecycle.ResolveState(goal, ReadJournalFacts(goal)));
            var result = driver.ProjectGateReadyCandidate(goal, ConductorAutonomyPolicy.Conservative);
            var projection = Assert.IsType<GateReadyCandidateProjectionResult.Ready>(result).Projection;
            Assert.Equal(goal.Id, projection.GoalId);
            Assert.Equal(GoalLifecycleState.Verified, projection.LifecycleState);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
