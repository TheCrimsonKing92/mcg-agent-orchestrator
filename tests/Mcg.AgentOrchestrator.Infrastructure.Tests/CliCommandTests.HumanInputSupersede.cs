using Mcg.AgentOrchestrator.App.Dashboard.Api;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CliCommandTestsHumanInputSupersede : CliCommandTestBase
{
    [Xunit.Fact]
    public void SupersedeCommand_ReportsAuthoritativeAnswer_AndMapperReturnsAuditHistory()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Wait for a choice.", AgentRole.Planner);
        var goal = kernel.CreateGoal("Correct a clarification.", [task]);
        var request = kernel.RequestHumanInput(goal.Id, task.Id, "Which value?");
        kernel.SubmitHumanInput(request.Id, "Use one second.");

        var output = ExecuteCliAndCapture(
            ["supersede", goal.Id.Value[..8], request.Id.Value[..8], "Use five seconds."],
            kernel,
            workspace);
        var dto = DashboardResponseMapper.ToHumanInputDto(kernel, request);

        Assert.Contains("Authoritative answer: Use five seconds.", output, StringComparison.Ordinal);
        Assert.Equal("Use five seconds.", dto.Answer);
        Assert.Equal(2, dto.AnswerHistory.Count);
        Assert.True(dto.AnswerHistory[0].IsRetracted);
        Assert.False(dto.AnswerHistory[0].IsAuthoritative);
        Assert.Equal(dto.AnswerHistory[1].Id, dto.AnswerHistory[0].SupersededByAnswerId);
        Assert.True(dto.AnswerHistory[1].IsAuthoritative);
        Assert.Equal("Use one second.", dto.AnswerHistory[0].Text);
    }

    [Xunit.Fact]
    public void SupersedeCommand_ScopesRequestLookupToOwningGoal()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var owner = kernel.CreateGoal("Owner", [new TaskSpec(TaskId.New(), "Plan.", AgentRole.Planner)]);
        var other = kernel.CreateGoal("Other", [new TaskSpec(TaskId.New(), "Plan.", AgentRole.Planner)]);
        var request = kernel.RequestHumanInput(owner.Id, owner.Tasks.Single().Id, "Which value?");
        kernel.SubmitHumanInput(request.Id, "A");

        var error = Assert.ThrowsAny<KeyNotFoundException>(() => ExecuteCliAndCapture(
            ["supersede", other.Id.Value[..8], request.Id.Value[..8], "B"],
            kernel,
            workspace));

        Assert.Contains("was not found on goal", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("belongs to a different goal", error.Message, StringComparison.Ordinal);
        Assert.Equal("A", request.Answer);
    }
}
