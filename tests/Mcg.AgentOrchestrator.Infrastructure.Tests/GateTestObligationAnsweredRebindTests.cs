using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each kernel, attempt and TRX file belongs to its isolated fixture.
public sealed class GateTestObligationAnsweredRebindTests
{
    [Theory]
    [InlineData("OldNameTests", "")]
    [InlineData("OldNameTests.Check", "")]
    [InlineData("Fixture.OldNameTests.Check", "")]
    [InlineData("OldNameTests", ".")]
    [InlineData("OldNameTests.Check", ".")]
    [InlineData("Fixture.OldNameTests.Check", ".")]
    public void OperatorAnswer_PassingNewRows_SatisfiesOriginalSelector(string selector, string suffix)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var kernel = GateTestObligationIntakeTests.Seed(fixture.Kernel, fixture.Goal, [selector]);
        var goal = kernel.GetGoal(fixture.Goal.Id);
        var request = Answer(kernel, goal, suffix);
        var rowClass = selector.StartsWith("Fixture.", StringComparison.Ordinal) ? "Fixture.NewNameTests" : "NewNameTests";
        var (id, _, _) = fixture.WriteAttempt([($"{rowClass}.Check", "Passed"), ($"{rowClass}.Second", "Passed")]);
        var baseline = Assert.Single(GateTestObligationEvaluator.Evaluate(goal,
            fixture.OrchestratorDirectory, fixture.CandidateSha, null));

        var result = Assert.Single(GateTestObligationEvaluator.Evaluate(goal,
            fixture.OrchestratorDirectory, fixture.CandidateSha, null, kernel.HumanInputRequests));

        Assert.Equal(GateTestObligationEvaluator.Outcome.SelectorNotFound, baseline.Outcome);
        Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, result.Outcome);
        Assert.Equal(id, result.SourceId);
        Assert.Equal(new[] { selector }, result.Selectors);
        var rebind = Assert.Single(result.AnswerRebinds!);
        Assert.Equal("OldNameTests", rebind.OldClass);
        Assert.Equal("NewNameTests", rebind.NewClass);
        Assert.Equal(request.Id.Value, rebind.RequestId);
        Assert.Equal(request.AuthoritativeAnswer!.Id, rebind.AnswerId);
        Assert.Equal($"GATE_TEST_OBLIGATION_ANSWER_REBOUND id={Assert.Single(goal.GateTestObligations).Id[..8]} " +
            $"old=OldNameTests new=NewNameTests request={request.Id.Value[..8]} " +
            $"answer={request.AuthoritativeAnswer.Id} source={id}", result.AnswerRebindProvenance);
    }

    [Theory]
    [InlineData("unrelated", "")]
    [InlineData("failed", "")]
    [InlineData("partial", "")]
    [InlineData("other-goal", "")]
    [InlineData("segment-substring", "")]
    [InlineData("ambiguous-segments", "")]
    [InlineData("skipped", "")]
    [InlineData("existing-failure", "")]
    [InlineData("unrelated", ".")]
    [InlineData("failed", ".")]
    [InlineData("skipped", ".")]
    public void UnsuccessfulRebind_PreservesBaselineGrading(string scenario, string suffix)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        string[] selectors = scenario switch
        {
            "partial" => ["OldNameTests", "MissingTests"],
            "segment-substring" => ["OtherOldNameTests"],
            "ambiguous-segments" => ["OldNameTests.OldNameTests.Check"],
            _ => ["OldNameTests"]
        };
        var kernel = GateTestObligationIntakeTests.Seed(fixture.Kernel, fixture.Goal, selectors);
        var goal = kernel.GetGoal(fixture.Goal.Id);
        var answerGoal = scenario == "other-goal"
            ? kernel.CreateGoal("Other goal", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]) : goal;
        Answer(kernel, answerGoal, suffix);
        (string, string)[] rows = scenario switch
        {
            "unrelated" => [("OtherWorkTests.Check", "Passed")],
            "failed" => [("NewNameTests.Check", "Failed")],
            "skipped" => [("NewNameTests.Check", "NotExecuted")],
            "existing-failure" => [("OldNameTests.Check", "Failed"), ("NewNameTests.Check", "Passed")],
            _ => [("NewNameTests.Check", "Passed")]
        };
        fixture.WriteAttempt(rows);
        var baseline = Assert.Single(GateTestObligationEvaluator.Evaluate(goal,
            fixture.OrchestratorDirectory, fixture.CandidateSha, null, null));

        var result = Assert.Single(GateTestObligationEvaluator.Evaluate(goal,
            fixture.OrchestratorDirectory, fixture.CandidateSha, null, kernel.HumanInputRequests));

        Assert.Equal(baseline.Outcome, result.Outcome);
        Assert.Equal(baseline.Selectors, result.Selectors);
        Assert.Equal(baseline.SourceId, result.SourceId);
        Assert.Empty(result.AnswerRebinds ?? []);
        Assert.Equal("", result.AnswerRebindProvenance);
        if (scenario == "unrelated")
        {
            Assert.Equal(GateTestObligationEvaluator.Outcome.SelectorNotFound, result.Outcome);
            Assert.Equal(new[] { "OldNameTests" }, result.Selectors);
        }
    }

    [Fact]
    public void AlreadySatisfiedSelector_WithAnswer_DoesNotRecordRebind()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var kernel = GateTestObligationIntakeTests.Seed(fixture.Kernel, fixture.Goal, ["NewNameTests"]);
        var goal = kernel.GetGoal(fixture.Goal.Id);
        Answer(kernel, goal);
        fixture.WriteAttempt([("NewNameTests.Check", "Passed")]);

        var result = Assert.Single(GateTestObligationEvaluator.Evaluate(goal,
            fixture.OrchestratorDirectory, fixture.CandidateSha, null, kernel.HumanInputRequests));

        Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, result.Outcome);
        Assert.Null(result.AnswerRebinds);
    }

    [Fact]
    public void RepeatedClassSelectors_OneSource_RecordsOneRebind()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var kernel = GateTestObligationIntakeTests.Seed(fixture.Kernel, fixture.Goal,
            ["OldNameTests.Check", "OldNameTests.Second"]);
        var goal = kernel.GetGoal(fixture.Goal.Id);
        Answer(kernel, goal);
        var (id, _, _) = fixture.WriteAttempt([("NewNameTests.Check", "Passed"), ("NewNameTests.Second", "Passed")]);

        var result = Assert.Single(GateTestObligationEvaluator.Evaluate(goal,
            fixture.OrchestratorDirectory, fixture.CandidateSha, null, kernel.HumanInputRequests));

        Assert.Equal(GateTestObligationEvaluator.Outcome.Satisfied, result.Outcome);
        Assert.Equal(id, result.SourceId);
        Assert.Single(result.AnswerRebinds!);
    }

    private static HumanInputRequest Answer(AgentOrchestratorKernel kernel, Goal goal, string suffix = "")
    {
        var request = kernel.RequestHumanInput(goal.Id, null, "Rename question");
        kernel.SubmitHumanInput(request.Id, "- OldNameTests becomes NewNameTests" + suffix);
        return request;
    }
}
