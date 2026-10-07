using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: all goal state and console capture are local to the test.
public sealed class CriterionEvidenceNumberLabelTests
{
    [Fact]
    public void DescribeCriterionUsesBriefNumberBesideUnchangedObligationId()
    {
        Assert.Equal("criterion 7 (v2) criterion-v2-6", CriterionEvidenceObligation.DescribeCriterion(2, 6));
    }

    [Fact]
    public void StoredZeroBasedObligationRestoresAndPrintsItsBriefNumber()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Keep criterion identity through restoration");
        var spec = new RefinedSpec("Number criteria for operators",
            Enumerable.Range(1, 7).Select(number => $"Requirement {number}").ToArray(),
            VerificationClass.TestVerifiable, [], []);
        kernel.RecordGoalRefinement(goal.Id, spec);
        kernel.RecordGoalRefinement(goal.Id, spec);
        kernel.MapCriterionEvidenceOwner(goal.Id, 6, 2, CriterionEvidenceOwner.Operator,
            "operator", expectedCandidateSha: "candidate-a");
        var json = JsonSerializer.Serialize(kernel.ExportGoalSnapshot(goal.Id));
        Assert.Contains("criterion-v2-6", json, StringComparison.Ordinal);
        Assert.DoesNotContain("CriterionNumber", json, StringComparison.Ordinal);
        Assert.DoesNotContain("DisplayLabel", json, StringComparison.Ordinal);
        var snapshot = JsonSerializer.Deserialize<GoalSnapshot>(json)!;
        var stored = Assert.Single(snapshot.CriterionEvidenceObligations!);
        Assert.Equal("criterion-v2-6", stored.Id);
        Assert.Equal(6, stored.CriterionIndex);

        var restored = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([snapshot], []))
            .GetGoal(goal.Id);

        var obligation = Assert.Single(restored.CriterionEvidenceObligations);
        Assert.Equal("criterion-v2-6", obligation.Id);
        Assert.Equal(6, obligation.CriterionIndex);
        Assert.Equal(7, obligation.CriterionNumber);
        Assert.Equal("criterion 7 (v2) criterion-v2-6", obligation.DisplayLabel);
        var output = InfrastructureTestSupport.CaptureConsole(() => ConsoleViews.PrintGoal(restored));
        Assert.Contains(output.Split(Environment.NewLine), line =>
            line.StartsWith("  - criterion 7 (v2) criterion-v2-6: owner=", StringComparison.Ordinal));
    }
}
