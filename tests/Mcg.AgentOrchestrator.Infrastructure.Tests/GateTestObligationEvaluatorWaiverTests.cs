using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every fixture owns its kernel, stores and temporary files.
public sealed class GateTestObligationEvaluatorWaiverTests
{
    private static readonly string[] Criteria = ["The first criterion passes.", "The second criterion passes."];

    [Fact]
    public void ExactTextWaiver_ChangesPendingToWaivedWithRecordedReason()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var kernel = CreateKernel(fixture);
        var goal = kernel.GetGoal(fixture.Goal.Id);
        Assert.Equal(GateTestObligationEvaluator.Outcome.Pending, Evaluate(fixture, goal).Outcome);

        var waiver = Waive(kernel, goal, 1, "Operator accepts this missing evidence.");
        var result = Evaluate(fixture, goal);

        Assert.Equal(GateTestObligationEvaluator.Outcome.Waived, result.Outcome);
        Assert.Equal(Assert.Single(goal.GateTestObligations).Selectors, result.Selectors);
        Assert.Null(result.SourceId);
        Assert.Equal(waiver.WaiverReason, result.WaiverReason);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(null, 1)]
    [InlineData(3, 1)]
    public void UnlinkedWaiver_PreservesBaselineGrading(int? linkedCriterion, int waivedCriterion)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var kernel = CreateKernel(fixture, linkedCriterion);
        var goal = kernel.GetGoal(fixture.Goal.Id);
        var baseline = Evaluate(fixture, goal);
        Assert.Equal(GateTestObligationEvaluator.Outcome.Pending, baseline.Outcome);

        Waive(kernel, goal, waivedCriterion, "Waive a criterion without a valid obligation link.");
        if (linkedCriterion == 3) Waive(kernel, goal, 2, "Waive the other criterion too.");

        AssertSameGrading(baseline, Evaluate(fixture, goal));
    }

    [Fact]
    public void OrdinalReference_ResolvesToSameWaiverAsExactText()
    {
        using var textFixture = new ProspectiveEvidenceGateFixture();
        using var ordinalFixture = new ProspectiveEvidenceGateFixture();
        var textKernel = CreateKernel(textFixture);
        var ordinalKernel = CreateKernel(ordinalFixture);
        var textGoal = textKernel.GetGoal(textFixture.Goal.Id);
        var ordinalGoal = ordinalKernel.GetGoal(ordinalFixture.Goal.Id);
        const string reason = "The operator waives the linked criterion.";

        var textWaiver = Waive(textKernel, textGoal, 1, reason);
        var ordinalWaiver = Waive(ordinalKernel, ordinalGoal, 1, reason, reference: "1");
        var textResult = Evaluate(textFixture, textGoal);
        var ordinalResult = Evaluate(ordinalFixture, ordinalGoal);

        Assert.Equal(textWaiver.SupersededCriterion, ordinalWaiver.SupersededCriterion);
        Assert.Equal(GateTestObligationEvaluator.Outcome.Waived, textResult.Outcome);
        Assert.Equal(GateTestObligationEvaluator.Outcome.Waived, ordinalResult.Outcome);
        AssertSameGrading(textResult, ordinalResult);
        Assert.Equal(reason, ordinalResult.WaiverReason);
        Assert.Equal(textResult.WaiverReason, ordinalResult.WaiverReason);
    }

    [Fact]
    public void DescribeWaiver_TruncatesLongReasonAndReturnsNullBeforeWaiver()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var kernel = CreateKernel(fixture);
        var goal = kernel.GetGoal(fixture.Goal.Id);
        var obligation = Assert.Single(goal.GateTestObligations);
        Assert.Null(GateTestObligationWaivers.DescribeWaiver(goal, obligation));
        var reason = new string('r', 300);

        Waive(kernel, goal, 1, reason);

        Assert.Equal("waived: " + reason[..120] + "...",
            GateTestObligationWaivers.DescribeWaiver(goal, obligation));
    }

    [Fact]
    public void MatchingWaivers_SelectsLastRecordedReasonIgnoringCriterionCase()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var kernel = CreateKernel(fixture);
        var goal = kernel.GetGoal(fixture.Goal.Id);
        Waive(kernel, goal, 1, "Earlier reason.");
        var newest = Waive(kernel, goal, 1, "Latest reason.", actor: "second-operator",
            reference: "  " + Criteria[0].ToUpperInvariant() + "  ");

        var matched = GateTestObligationWaivers.FindWaiver(goal, Assert.Single(goal.GateTestObligations));
        Assert.NotNull(matched);
        Assert.Equal(newest.SupersededCriterion, matched.SupersededCriterion);
        Assert.Equal(newest.Actor, matched.Actor);
        Assert.Equal(newest.WaiverReason, matched.WaiverReason);
        Assert.Equal("Latest reason.", Evaluate(fixture, goal).WaiverReason);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void InvalidCriterionIndex_DoesNotMatchWaiver(int index)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var kernel = CreateKernel(fixture, index);
        var goal = kernel.GetGoal(fixture.Goal.Id);
        var baseline = Evaluate(fixture, goal);
        Waive(kernel, goal, 1, "No valid criterion link.");

        Assert.Null(GateTestObligationWaivers.FindWaiver(goal, Assert.Single(goal.GateTestObligations)));
        AssertSameGrading(baseline, Evaluate(fixture, goal));
    }

    [Fact]
    public void MissingRefinedSpec_DoesNotMatchWaiver()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var kernel = SeedPlannerObligation(fixture.Kernel, fixture.Goal);
        var goal = kernel.GetGoal(fixture.Goal.Id);

        Assert.Null(goal.RefinedSpec);
        Assert.Null(GateTestObligationWaivers.FindWaiver(goal, Assert.Single(goal.GateTestObligations)));
        Assert.Equal(GateTestObligationEvaluator.Outcome.Pending, Evaluate(fixture, goal).Outcome);
    }

    [Theory]
    [InlineData(" \talpha\r\n beta\u2003gamma  ", "alpha beta gamma")]
    [InlineData("", "")]
    public void FormatReason_CollapsesDotNetWhitespace(string reason, string expected) =>
        Assert.Equal(expected, GateTestObligationWaivers.FormatReason(reason));

    [Fact]
    public void FormatReason_AppliesLimitAfterNormalization()
    {
        var atLimit = new string('r', 120);
        Assert.Equal(atLimit, GateTestObligationWaivers.FormatReason(" \t" + atLimit + "\r\n"));
        Assert.Equal(atLimit + "...", GateTestObligationWaivers.FormatReason(atLimit + "r"));
    }

    internal static void RecordRefinement(AgentOrchestratorKernel kernel, Goal goal, string candidate)
    {
        kernel.RecordGoalRefinement(goal.Id,
            new RefinedSpec(goal.Objective, Criteria, VerificationClass.TestVerifiable, [], []));
        for (var index = 0; index < Criteria.Length; index++)
            kernel.MapCriterionEvidenceOwner(goal.Id, index, goal.AuthoritativeBrief.Version,
                CriterionEvidenceOwner.Acceptance, "test", CriterionEvidenceScopes.FullAcceptanceGate,
                expectedCandidateSha: candidate);
    }

    internal static AgentOrchestratorKernel SeedPlannerObligation(
        AgentOrchestratorKernel kernel, Goal goal, int criterionIndex = 1)
    {
        var identity = new PlannerEvidenceIdentity(goal.Id.Value, "planner", goal.AuthoritativeBrief.Version,
            criterionIndex, "gate");
        var snapshot = kernel.ExportSnapshot();
        return AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(item => item.Id == goal.Id.Value ? item with
            {
                GateTestObligations = [new(identity.StableId, goal.AuthoritativeBrief.Version, ["MissingTests"],
                    "planner", "Candidate gate receipt", DateTimeOffset.UnixEpoch, identity)]
            } : item).ToArray()
        });
    }

    internal static EffectiveAcceptanceCriteriaCorrection Waive(
        AgentOrchestratorKernel kernel, Goal goal, int criterionNumber, string reason,
        string? candidate = null, string actor = "operator", string? reference = null)
    {
        var criterion = goal.RefinedSpec!.AcceptanceCriteria[criterionNumber - 1].Trim();
        var dispositions = goal.GetOutstandingCriterionEvidenceObligations(candidate)
            .Where(item => !string.Equals(item.Criterion.Trim(), criterion, StringComparison.OrdinalIgnoreCase))
            .Where(item => !goal.EffectiveAcceptanceCriteriaCorrections.Any(correction => correction.IsWaiver &&
                string.Equals(correction.SupersededCriterion, item.Criterion.Trim(), StringComparison.OrdinalIgnoreCase)))
            .Select(item => item.Criterion.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(text => new CriterionDispositionRequest(text, "Acceptance still owns the remaining evidence."))
            .ToArray();
        return kernel.WaiveAcceptanceCriterion(goal.Id, reference ?? criterion, reason, actor,
            dispositions, currentCandidateSha: candidate);
    }

    private static AgentOrchestratorKernel CreateKernel(ProspectiveEvidenceGateFixture fixture, int? criterionIndex = 1)
    {
        RecordRefinement(fixture.Kernel, fixture.Goal, fixture.CandidateSha);
        fixture.Kernel.ActivateGoal(fixture.Goal.Id, AgentCatalog.Default().Agents);
        return criterionIndex is { } index
            ? SeedPlannerObligation(fixture.Kernel, fixture.Goal, index)
            : GateTestObligationIntakeTests.Seed(fixture.Kernel, fixture.Goal, ["MissingTests"]);
    }

    private static GateTestObligationEvaluator.Result Evaluate(ProspectiveEvidenceGateFixture fixture, Goal goal) =>
        Assert.Single(GateTestObligationEvaluator.Evaluate(goal, fixture.OrchestratorDirectory, fixture.CandidateSha));

    private static void AssertSameGrading(GateTestObligationEvaluator.Result expected, GateTestObligationEvaluator.Result actual)
    {
        Assert.Equal(expected.Outcome, actual.Outcome);
        Assert.Equal(expected.Selectors, actual.Selectors);
        Assert.Equal(expected.SourceId, actual.SourceId);
    }
}
