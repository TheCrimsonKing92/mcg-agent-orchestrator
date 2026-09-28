using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ConductorAcceptanceCohortSelectorTestsAttributedMemberExclusion
{
    [Fact]
    public void AttributedRevision_IsExcludedFromNewCohortAndTrain_UntilItChanges()
    {
        var database = Path.Combine(Path.GetTempPath(), $"cohort-attributed-{Guid.NewGuid():N}.db");
        try
        {
            var store = new CohortAcceptanceStore(database);
            var a = Ready('1', 'a');
            var b = Ready('2', 'b');
            var c = Ready('3', 'c');
            var d = Ready('4', 'd');
            var members = new[] { a, b }.Select(candidate =>
            {
                var projection = ((GateReadyCandidateProjectionResult.Ready)candidate.ProjectionResult).Projection;
                return new AcceptanceCohortMemberBinding(candidate.GoalId, projection.BranchRevision,
                    projection.CandidateRevision, projection.LandingPaths, projection.ResourceKeys,
                    projection.ChangeRiskTier, projection.AutoPromotionDisposition,
                    projection.MergeEvidence.Status.ToString(), projection.MergeEvidence.Reason.ToString());
            }).ToArray();
            var identity = AcceptanceCohortIdentity.Create(members,
                new string('f', 40), new string('e', 40), "manifest");
            store.SaveGateReceipt(new AcceptanceCohortReceipt("receipt", identity,
                AcceptanceCohortGateOutcome.Failed, DateTimeOffset.UtcNow, 1, ["red"], 1, []));
            Assert.Empty(store.ReadAttributedMemberKeys());
            var partitions = members.Select((member, index) => new AcceptanceCohortPartitionReceipt(
                $"partition-{index}", member.GoalId, index, member.CandidateRevision,
                identity.ObservedMainRevision, member.CandidateRevision, identity.ManifestIdentity,
                index == 0 ? AcceptanceCohortGateOutcome.Failed : AcceptanceCohortGateOutcome.Passed, 1, []))
                .ToArray();
            store.SaveAttribution(identity.Value, AcceptanceCohortAttributionOutcome.FirstMemberFailed,
                partitions, "pair", b.GoalId,
                [new AcceptanceCohortAttributedMember(a.GoalId, 0, members[0].CandidateRevision, ["Tests.T"])], []);
            var keys = store.ReadAttributedMemberKeys();
            Assert.Contains(ConductorAcceptanceCohortAttributedMembers.Key(a.GoalId, members[0].CandidateRevision), keys);

            var cohort = ConductorAcceptanceCohortSelector.Select([a, b, c], attributedMemberKeys: keys);
            Assert.Equal([b.GoalId, c.GoalId], cohort.Selection!.Members.Select(member => member.GoalId));
            Assert.Contains(cohort.Exclusions, exclusion => exclusion.FirstGoalId == a.GoalId &&
                exclusion.Reason == ConductorAcceptanceCohortPairExclusionReason.AttributedMember);
            var train = ConductorMergeTrainSelector.Select([a, b, c, d], attributedMemberKeys: keys);
            Assert.Equal([b.GoalId, c.GoalId, d.GoalId], train!.Members.Select(member => member.GoalId));

            var changed = Ready('1', 'e');
            Assert.Equal(a.GoalId, changed.GoalId);
            Assert.Equal([a.GoalId, b.GoalId],
                ConductorAcceptanceCohortSelector.Select([changed, b, c], attributedMemberKeys: keys)
                    .Selection!.Members.Select(member => member.GoalId));
            Assert.Equal([a.GoalId, b.GoalId, c.GoalId],
                ConductorMergeTrainSelector.Select([changed, b, c, d], attributedMemberKeys: keys)!
                    .Members.Select(member => member.GoalId));
        }
        finally
        {
            if (File.Exists(database)) File.Delete(database);
        }
    }

    private static ConductorSpeculativeAcceptanceCandidate Ready(char goal, char revision)
    {
        var goalId = new GoalId(new string(goal, 32));
        var projection = new GateReadyCandidateProjection(goalId, GoalLifecycleState.Verified,
            GateReadyVerificationState.Satisfied, ChangeRiskTier.Behavior,
            ConductorTransitionDecision.Auto, [$"src/{goal}.cs"], [$"resource:{goal}"],
            new GateReadyMergeEvidence(new string(revision, 40), new string('f', 40),
                GateReadyMergeStatus.Clean, GateReadyMergeReason.NoConflictsDetected));
        return new ConductorSpeculativeAcceptanceCandidate(goalId,
            new GateReadyCandidateProjectionResult.Ready(projection));
    }
}
