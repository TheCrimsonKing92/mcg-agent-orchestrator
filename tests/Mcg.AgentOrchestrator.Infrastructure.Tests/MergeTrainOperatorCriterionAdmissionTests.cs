using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

using static ConductorDriverTests;

public sealed class MergeTrainOperatorCriterionAdmissionTests
{
    [Fact]
    public void MissingRefinementOrCurrentObligationsExcludesOnlyThoseTrainMembers()
    {
        var (_, noSpec) = SimpleGoal("No authoritative refined spec");
        var (workerKernel, noObligations) = SimpleGoal("Refined with worker criterion only");
        workerKernel.RecordGoalRefinement(noObligations.Id, new RefinedSpec(
            noObligations.Objective, ["Worker checks output"], VerificationClass.TestVerifiable, [], []));

        var (eligibleKernel, eligible) = SimpleGoal("Acceptance owns a criterion");
        eligibleKernel.RecordGoalRefinement(eligible.Id, new RefinedSpec(
            eligible.Objective, ["Full gate passes"], VerificationClass.TestVerifiable, [], [])
        {
            AcceptanceGateOwnedAcceptanceCriteria = ["Full gate passes"]
        });

        var excluded = ConductorBatchLoop.TrainIneligibleCriterionEvidenceGoalIds(
            [noSpec, noObligations, eligible]);

        Assert.Contains(noSpec.Id.Value, excluded);
        Assert.Contains(noObligations.Id.Value, excluded);
        Assert.DoesNotContain(eligible.Id.Value, excluded);
        Assert.Contains("required criterion evidence remains outstanding",
            AcceptanceCriterionEvidence.DescribeTrainOperatorEvidenceGap(noSpec));
    }

    [Fact]
    public void PendingOperatorObligationExcludesMemberAndUsesSoloOutstandingMessage()
    {
        var (kernel, goal) = SimpleGoal("Operator observes output");
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(
            goal.Objective, ["Operator observes output"], VerificationClass.TestVerifiable, [], [])
        {
            OperatorOwnedAcceptanceCriteria = ["Operator observes output"]
        });

        Assert.Contains(goal.Id.Value,
            ConductorBatchLoop.TrainIneligibleCriterionEvidenceGoalIds([goal]));
        Assert.Null(AcceptanceCriterionEvidence.DescribeTrainOperatorEvidenceGap(goal));
        Assert.Equal(
            "Acceptance completed but required criterion evidence remains outstanding: criterion-v1-0:Operator:Pending:next=operator observation.",
            AcceptanceCriterionEvidence.RecordAndDescribeOutstanding(goal, new string('a', 40), kernel));
    }
}
