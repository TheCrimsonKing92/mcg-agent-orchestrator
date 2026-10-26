using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each kernel and temporary evidence directory belongs to its fixture.
public sealed class AnsweredSelectorRebindingTests
{
    [Fact]
    public void Parse_IncidentAnswer_ReturnsOrderedClassPairs()
    {
        const string answer = """
            Correct. Rename the two classes everywhere the brief names them:
            - WorkerBuildRequestGcEnvironmentTests becomes GcEnvironmentBuildChannelTests (file GcEnvironmentBuildChannelTests.cs);
            - CompletionBuildGcEnvironmentTests becomes GcEnvironmentEvidenceLaunchTests (file GcEnvironmentEvidenceLaunchTests.cs).
            Every assertion in those criteria stays as written.
            """;

        Assert.Equal(new[]
        {
            ("WorkerBuildRequestGcEnvironmentTests", "GcEnvironmentBuildChannelTests"),
            ("CompletionBuildGcEnvironmentTests", "GcEnvironmentEvidenceLaunchTests")
        }, AnsweredSelectorRebinding.Parse(answer));
    }

    [Theory]
    [InlineData("OldNameTests -> NewNameTests")]
    [InlineData("OldNameTests BECOMES NewNameTests")]
    [InlineData("`OldNameTests` becomes `NewNameTests`")]
    public void Parse_ExplicitRename_ReturnsPair(string answer) =>
        Assert.Equal(new[] { ("OldNameTests", "NewNameTests") }, AnsweredSelectorRebinding.Parse(answer));

    [Theory]
    [InlineData("oldNameTests becomes NewNameTests")]
    [InlineData("OldNameTests becomes OldNameTests")]
    [InlineData("The class becomes flaky under load.")]
    [InlineData("Rename OldNameTests to NewNameTests.")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Namespace.OldNameTests becomes NewNameTests")]
    [InlineData("OldNameTests becomes Namespace.NewNameTests")]
    [InlineData("OldNameTests becomes NewNameTests.Check")]
    [InlineData("_OldNameTests becomes NewNameTests")]
    [InlineData("1OldNameTests becomes NewNameTests")]
    [InlineData("old\u0301OldNameTests becomes NewNameTests")]
    [InlineData("`OldNameTests becomes NewNameTests")]
    [InlineData("OldNameTests becomes NewNameTests`")]
    [InlineData("``OldNameTests`` becomes NewNameTests")]
    public void Parse_NonRenameOrNonIdentifier_ReturnsNoPairs(string? answer) =>
        Assert.Empty(AnsweredSelectorRebinding.Parse(answer));

    [Fact]
    public void Parse_RepeatedPairs_PreservesFirstOccurrenceOrder()
    {
        Assert.Equal(new[] { ("OldNameTests", "NewNameTests"), ("OtherTests", "NextTests") },
            AnsweredSelectorRebinding.Parse("OldNameTests becomes NewNameTests; " +
                "OtherTests -> NextTests; OldNameTests -> NewNameTests"));
    }

    [Fact]
    public void Parse_UnicodeIdentifierParts_PreservesNames()
    {
        Assert.Equal(new[] { ("Old_\u2160\u0301Tests", "New_\u2161\u0301Tests") },
            AnsweredSelectorRebinding.Parse("Old_\u2160\u0301Tests becomes New_\u2161\u0301Tests"));
    }

    [Fact]
    public void For_CurrentOperatorAnswer_ReturnsAnswerIdentity()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var kernel = GateTestObligationIntakeTests.Seed(fixture.Kernel, fixture.Goal, ["OldNameTests"]);
        var goal = kernel.GetGoal(fixture.Goal.Id);
        var request = kernel.RequestHumanInput(goal.Id, null, "Rename question");
        kernel.SubmitHumanInput(request.Id, "- OldNameTests becomes NewNameTests");

        var rebind = Assert.Single(AnsweredSelectorRebinding.For(goal,
            Assert.Single(goal.GateTestObligations), kernel.HumanInputRequests));

        Assert.Equal("OldNameTests", rebind.OldClass);
        Assert.Equal("NewNameTests", rebind.NewClass);
        Assert.Equal(request.Id.Value, rebind.RequestId);
        Assert.Equal(request.AuthoritativeAnswer!.Id, rebind.AnswerId);
    }

    [Theory]
    [InlineData("worker")]
    [InlineData("other-goal")]
    [InlineData("old-brief")]
    [InlineData("conflict")]
    [InlineData("pending")]
    [InlineData("dismissed")]
    public void For_IneligibleOrConflictingAnswer_ReturnsNoRebinds(string scenario)
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var kernel = GateTestObligationIntakeTests.Seed(fixture.Kernel, fixture.Goal, ["OldNameTests"]);
        var goal = kernel.GetGoal(fixture.Goal.Id);
        var answerGoal = scenario == "other-goal"
            ? kernel.CreateGoal("Other goal", [new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]) : goal;
        const string answer = "- OldNameTests becomes NewNameTests";
        var request = kernel.RequestHumanInput(answerGoal.Id, null, scenario == "pending" ? answer : "Rename question");
        if (scenario == "dismissed")
            kernel.DismissHumanInput(request.Id);
        else if (scenario != "pending")
            kernel.SubmitHumanInput(request.Id, answer,
                origin: scenario == "worker" ? HumanInputAnswerOrigin.Worker : HumanInputAnswerOrigin.Operator);
        if (scenario == "old-brief")
        {
            kernel.ReviseGoalBrief(goal.Id, "Revised gate evidence");
            kernel = GateTestObligationIntakeTests.Seed(kernel, goal, ["OldNameTests"]);
            goal = kernel.GetGoal(goal.Id);
            Assert.NotEqual(request.AuthoritativeAnswer!.BriefVersion, Assert.Single(goal.GateTestObligations).BriefVersion);
        }
        if (scenario == "conflict")
        {
            var other = kernel.RequestHumanInput(goal.Id, null, "Another rename question");
            Assert.NotEqual(request.Id, other.Id);
            kernel.SubmitHumanInput(other.Id, "OldNameTests becomes OtherNameTests");
        }

        Assert.Empty(AnsweredSelectorRebinding.For(goal, Assert.Single(goal.GateTestObligations), kernel.HumanInputRequests));
    }

    [Fact]
    public void For_RepeatedMapping_KeepsEarliestAnswerRegardlessOfInputOrder()
    {
        using var fixture = new ProspectiveEvidenceGateFixture();
        var kernel = GateTestObligationIntakeTests.Seed(fixture.Kernel, fixture.Goal, ["OldNameTests"]);
        var goal = kernel.GetGoal(fixture.Goal.Id);
        var first = kernel.RequestHumanInput(goal.Id, null, "Rename question");
        kernel.SubmitHumanInput(first.Id, "OldNameTests becomes NewNameTests");
        var second = kernel.RequestHumanInput(goal.Id, null, "Confirm rename question");
        kernel.SubmitHumanInput(second.Id, "OldNameTests becomes NewNameTests");
        var expected = new[] { first, second }.OrderBy(request => request.AuthoritativeAnswer!.AnsweredAt)
            .ThenBy(request => request.AuthoritativeAnswer!.Id, StringComparer.Ordinal).First();

        var rebind = Assert.Single(AnsweredSelectorRebinding.For(goal,
            Assert.Single(goal.GateTestObligations), new[] { second, first }));

        Assert.Equal(expected.Id.Value, rebind.RequestId);
        Assert.Equal(expected.AuthoritativeAnswer!.Id, rebind.AnswerId);
    }
}
