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

    [Xunit.Fact]
    public async Task SupersedeCommand_CorrectsClosedSpecClarification_AndPreservesAuditHistory()
    {
        var root = CreateTempDirectory();
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Implement Use 5 seconds without changing Notice text.", AgentRole.Developer);
        var goal = kernel.CreateGoal("Prior artifact says Use 5 seconds. Notice text remains.", [task]);
        var correlationKey = $"spec-clarification:{goal.Id.Value}:minimum-backoff-values";
        kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec(
            "Use the selected backoff.",
            ["The selected backoff is applied."],
            VerificationClass.TestVerifiable,
            [new RefinedSpecDecision(
                "Which backoff?",
                "Use 5 seconds",
                $"Answered by operator (key: {correlationKey}).")],
            [new RefinedSpecOpenQuestion(
                correlationKey,
                "Which backoff?",
                "minimum-backoff-values",
                "Answered",
                "Use 5 seconds") ]));
        var store = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var item = await store.RaiseAsync(
            CollaborationItemType.Clarification,
            goal.Id.Value,
            "Minimum backoff values",
            "Which backoff?",
            correlationKey);
        Assert.True(await store.TryResolveAsync(correlationKey, "Use 5 seconds"));

        var ordinaryAnswer = Assert.Throws<InvalidOperationException>(() => ExecuteCliAndCapture(
            ["attention", "answer", goal.Id.Value[..8], "minimum-backoff-values", "5 seconds"],
            kernel,
            workspace));
        var output = ExecuteCliAndCapture(
            ["supersede", goal.Id.Value[..8], "minimum-backoff-values", "5 seconds"],
            kernel,
            workspace);
        var updated = Assert.Single((await store.ListAsync(goal.Id.Value)).Where(candidate => candidate.Id == item.Id));
        var historyOutput = ExecuteCliAndCapture(
            ["attention", "show", "--all", goal.Id.Value[..8]],
            kernel,
            workspace);
        var brief = kernel.BuildTaskBrief(goal.Id, task.Id).Content;

        Assert.Contains("already answered — use supersede", ordinaryAnswer.Message, StringComparison.Ordinal);
        Assert.Contains("Authoritative answer: 5 seconds", output, StringComparison.Ordinal);
        Assert.Equal("5 seconds", updated.Resolution);
        Assert.Equal(2, updated.AnswerHistory?.Count);
        Assert.True(updated.AnswerHistory![0].IsRetracted);
        Assert.Equal(updated.AnswerHistory[1].Id, updated.AnswerHistory[0].SupersededByAnswerId);
        Assert.False(updated.AnswerHistory[1].IsRetracted);
        Assert.Contains("retracted supersededBy=", historyOutput, StringComparison.Ordinal);
        Assert.Contains("authoritative: 5 seconds", historyOutput, StringComparison.Ordinal);
        Assert.Contains("5 seconds", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("Use 5 seconds", brief, StringComparison.Ordinal);
        Assert.Contains("Notice text remains", brief, StringComparison.Ordinal);
    }
}
