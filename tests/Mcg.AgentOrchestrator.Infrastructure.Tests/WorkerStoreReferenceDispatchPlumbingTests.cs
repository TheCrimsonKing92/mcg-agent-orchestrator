using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

public sealed class WorkerStoreReferenceDispatchPlumbingTests
{
    [Fact]
    public void PrepareTaskDeliversAnsweredPrerequisiteReferenceThroughTypedPackage()
    {
        using var fixture = new WorkerStoreReferenceFixture();
        fixture.WriteSource("operator-evidence/answer.md", "prerequisite record sentinel");
        var (kernel, goal, task) = fixture.CreateGoal("Implement scoped behavior.");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var request = kernel.RequestHumanInput(goal.Id, task.Id, "Which recorded criterion applies?", HumanWaitKind.PlannerPrerequisiteEvidence);
        kernel.SubmitHumanInput(request.Id, "store-ref: answer = operator-evidence:operator-evidence/answer.md");
        var resolver = CitedPriorEvidenceResolver.ForWorkspace(Path.Combine(fixture.StoreRoot, "state.db"), fixture.StoreRoot);

        var prepared = WorkerProfileDispatcher.PrepareTask(kernel, goal, task,
            new WorkerProfile("codex-cli", "codex exec --sandbox {sandboxMode} --cd {workingDirectory}"),
            Path.Combine(fixture.Root, "prompts"), fixture.WorkingDirectory, fixture.Clock.UtcNow,
            providerName: "OpenAI", modelName: AgentCatalog.OpenAiSolSubscriptionModelAlias, citedPriorEvidenceResolver: resolver);

        var context = Path.Combine(fixture.WorkingDirectory, ".orchestrator-context", goal.Id.Value);
        var content = File.ReadAllText(Path.Combine(context, "store-refs", "answer.md"));
        Assert.Equal("prerequisite record sentinel", WorkerStoreReferenceFixture.Excerpt(content));
        Assert.Contains($"Resolved at: {fixture.Clock.UtcNow:O}", content);
        var section = Assert.Single(prepared.Task.LastDispatch!.ContextPackageReceipt!.Sections,
            item => item.LogicalIdentity == "context/store-refs/answer.md");
        Assert.Equal(ContextDeliveryMode.InlineFull, section.DeliveryMode);
        var prompt = File.ReadAllText(prepared.PromptPath);
        Assert.Contains("identity=context/store-refs/answer.md", prompt);
        Assert.Contains("prerequisite record sentinel", prompt);
        Assert.Contains(WorkerStoreReferenceResolver.UntrustedBanner, prompt);
        Assert.False(File.Exists(Path.Combine(fixture.StoreRoot, "state.db")));
    }

    [Fact]
    public void PrepareTaskIgnoresAnswersFromOtherGoalsAndOtherRequestKinds()
    {
        using var fixture = new WorkerStoreReferenceFixture();
        fixture.WriteSource("operator-evidence/answer.md", "unselected");
        var (kernel, goal, task) = fixture.CreateGoal("Implement scoped behavior.");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var otherTask = new TaskSpec(TaskId.New(), "Other task.", AgentRole.Developer);
        var otherGoal = kernel.CreateGoal("Other objective.", [otherTask]);
        kernel.ActivateGoal(otherGoal.Id, AgentCatalog.Default().Agents);
        var otherGoalRequest = kernel.RequestHumanInput(otherGoal.Id, otherTask.Id, "Other prerequisite?", HumanWaitKind.PlannerPrerequisiteEvidence);
        kernel.SubmitHumanInput(otherGoalRequest.Id, "store-ref: other = operator-evidence:operator-evidence/answer.md");
        var clarification = kernel.RequestHumanInput(goal.Id, task.Id, "Clarification?", HumanWaitKind.SpecClarification);
        kernel.SubmitHumanInput(clarification.Id, "store-ref: clarification = operator-evidence:operator-evidence/answer.md");

        WorkerProfileDispatcher.PrepareTask(kernel, goal, task,
            new WorkerProfile("codex-cli", "codex exec --sandbox {sandboxMode} --cd {workingDirectory}"),
            Path.Combine(fixture.Root, "prompts"), fixture.WorkingDirectory, fixture.Clock.UtcNow,
            providerName: "OpenAI", modelName: AgentCatalog.OpenAiSolSubscriptionModelAlias,
            citedPriorEvidenceResolver: CitedPriorEvidenceResolver.ForWorkspace(Path.Combine(fixture.StoreRoot, "state.db"), fixture.StoreRoot));

        var context = Path.Combine(fixture.WorkingDirectory, ".orchestrator-context", goal.Id.Value);
        Assert.False(Directory.Exists(Path.Combine(context, "store-refs")));
    }
}
