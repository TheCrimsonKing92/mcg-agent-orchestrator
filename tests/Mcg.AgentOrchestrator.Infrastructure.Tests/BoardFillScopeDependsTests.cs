using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class BoardFillScopeDependsTests
{
    [Fact]
    public void Exactly_colliding_open_goals_are_proposed_with_advisor_reasons()
    {
        using var h = new BoardFillAssessmentTestFixture();
        var first = h.Kernel.CreateGoal("Change src/Feature/File.cs.");
        var second = h.Kernel.CreateGoal("Change src/Feature/File.cs and src/Other/File.cs.");
        h.Kernel.CreateGoal("Change src/Unrelated/File.cs.");
        var proposal = BoardFillScopeDepends.Propose(BoardFillAssessmentTestFixture.Brief, h.Kernel.Goals.ToArray(), h.Item.Id);
        Assert.Equal(new[] { first.Id.Value, second.Id.Value }.Order(), proposal.Depends.Select(dependency => dependency.GoalId));
        Assert.All(proposal.Depends, dependency => Assert.Equal("ExactFile src/Feature/File.cs<->src/Feature/File.cs", dependency.Reason));
        Assert.StartsWith("overlap-detected", proposal.Verdict);
    }
}
