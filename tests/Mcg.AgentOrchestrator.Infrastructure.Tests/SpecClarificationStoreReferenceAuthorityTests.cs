using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each case owns its kernel, SQLite database, files and clock.
public sealed class SpecClarificationStoreReferenceAuthorityTests
{
    [Theory]
    [InlineData("foreign")]
    [InlineData("sibling")]
    [InlineData("goal-level")]
    [InlineData("pending")]
    [InlineData("blank")]
    [InlineData("dismissed")]
    [InlineData("superseded")]
    [InlineData("parked")]
    public async Task IneligibleAnswer_PairedWithEligible_DeliversOnlyEligible(string state)
    {
        using var fixture = new WorkerStoreReferenceFixture();
        var retained = fixture.WriteSource("operator-evidence/retained.md", "retained evidence");
        fixture.WriteSource("operator-evidence/excluded.md", "excluded evidence");
        var kernel = new AgentOrchestratorKernel(fixture.Clock);
        var task = new TaskSpec(TaskId.New(), "Implement scope.", AgentRole.Developer);
        var sibling = new TaskSpec(TaskId.New(), "Inspect scope.", AgentRole.Tester);
        var goal = kernel.CreateGoal("Implement scope.", [task, sibling]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        fixture.Answer(kernel, goal, task.Id, "retained");
        HumanInputRequest excluded;
        switch (state)
        {
            case "foreign":
                var foreignTask = new TaskSpec(TaskId.New(), "Other scope.", AgentRole.Developer);
                var foreign = kernel.CreateGoal("Other scope.", [foreignTask]);
                kernel.ActivateGoal(foreign.Id, AgentCatalog.Default().Agents);
                excluded = fixture.Answer(kernel, foreign, foreignTask.Id, "excluded");
                break;
            case "sibling":
                excluded = fixture.Answer(kernel, goal, sibling.Id, "excluded");
                break;
            case "goal-level":
                excluded = fixture.Answer(kernel, goal, null, "excluded");
                break;
            case "superseded":
                excluded = kernel.RequestHumanInput(goal.Id, task.Id, "Shared evidence?");
                var replacing = kernel.RequestHumanInput(goal.Id, sibling.Id, "Shared evidence?");
                kernel.SubmitHumanInput(replacing.Id, "Initial sibling ruling.");
                kernel.SupersedeHumanInput(goal.Id, replacing.Id,
                    WorkerStoreReferenceFixture.Reference("excluded"), HumanInputAnswerOrigin.Operator);
                Assert.True(excluded.IsCompleted);
                Assert.Equal(replacing.Id, excluded.SupersededByRequestId);
                Assert.Equal(WorkerStoreReferenceFixture.Reference("excluded"), excluded.Answer);
                break;
            default:
                excluded = kernel.RequestHumanInput(goal.Id, task.Id,
                    WorkerStoreReferenceFixture.Reference("excluded"), isDismissible: true);
                if (state == "dismissed")
                {
                    kernel.DismissHumanInput(excluded.Id);
                    // Dismissal resumes Running; arrange a normal assigned redispatch without a process.
                    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "Redispatch after dismissal.");
                    kernel.RedelegateTask(goal.Id, task.Id, AgentCatalog.Default().Agents);
                }
                else if (state == "parked")
                {
                    kernel.ParkGoal(goal.Id, "Pause for owner.\n" + WorkerStoreReferenceFixture.Reference("excluded"));
                    kernel.UnparkGoal(goal.Id, "Resume scope.");
                    Assert.True(excluded.IsSyntheticParkedHumanWaitCompletion);
                    Assert.Contains("store-ref: excluded", excluded.Answer!);
                }
                else if (state == "blank")
                {
                    Assert.Throws<ArgumentException>(() => kernel.SubmitHumanInput(excluded.Id, " "));
                    kernel.SubmitHumanInput(excluded.Id, WorkerStoreReferenceFixture.Reference("excluded"));
                    // Legacy blank compatibility only; authority/supersession use public APIs above.
                    var snapshot = kernel.ExportSnapshot();
                    kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
                    {
                        HumanInputRequests = snapshot.HumanInputRequests.Select(item => item.Id == excluded.Id.Value
                            ? item with { AnswerHistory = item.AnswerHistory!.Select(answer => answer with { Text = " " }).ToList() }
                            : item).ToList()
                    }, fixture.Clock);
                }
                else
                {
                    Assert.Equal("pending", state);
                    Assert.False(excluded.IsCompleted);
                    Assert.Null(excluded.AuthoritativeAnswer);
                    // Exercise packaging while an unanswered request is present, without answering it.
                    kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "Inspect pending-input packaging.");
                    kernel.RedelegateTask(goal.Id, task.Id, AgentCatalog.Default().Agents);
                }
                break;
        }

        kernel = await fixture.Reload(kernel);
        goal = kernel.GetGoal(goal.Id);
        task = goal.FindTask(task.Id);
        var prepared = fixture.Prepare(kernel, goal, task);

        fixture.AssertDelivered(prepared, goal, "retained", retained, "retained evidence");
        fixture.AssertAbsent(prepared, goal, "excluded");
        if (state == "sibling")
        {
            var brief = kernel.BuildTaskBriefSource(goal.Id, task.Id).ProjectLegacyMarkedTextV1(false).Content;
            Assert.Contains("## Clarifications and rulings", brief);
            Assert.Contains(excluded.Id.Value, brief);
            Assert.Contains(WorkerStoreReferenceFixture.Reference("excluded"), brief);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnswerReplacement_NextDispatch_PreservesCapturedEarlierBytes(bool reviseBrief)
    {
        using var fixture = new WorkerStoreReferenceFixture();
        var firstSource = fixture.WriteSource("operator-evidence/original.md", "original evidence");
        var nextSource = fixture.WriteSource("operator-evidence/replacement.md", "replacement evidence");
        var (kernel, goal, task) = fixture.CreateGoal("Implement scoped behavior.");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var request = fixture.Answer(kernel, goal, task.Id, "original");
        kernel = await fixture.Reload(kernel);
        goal = kernel.GetGoal(goal.Id);
        task = goal.FindTask(task.Id);
        var first = fixture.Prepare(kernel, goal, task);
        fixture.AssertDelivered(first, goal, "original", firstSource, "original evidence");
        var capture = fixture.Capture(goal);
        var hashes = WorkerStoreReferenceFixture.Inventory(capture);
        Assert.NotEmpty(hashes);
        var promptHash = WorkerStoreReferenceFixture.Hash(first.PromptPath);
        var receipt = first.Task.LastDispatch!.ContextPackageReceipt!;

        fixture.Advance();
        var replacement = WorkerStoreReferenceFixture.Reference("replacement");
        if (reviseBrief)
            kernel.ReviseGoalBrief(goal.Id, "Implement revised scope.", answerSupersessions:
                [new GoalBriefAnswerSupersession(request.Id, replacement)]);
        else
            kernel.SupersedeHumanInput(goal.Id, request.Id, replacement, HumanInputAnswerOrigin.Operator);
        kernel = await fixture.Reload(kernel);
        goal = kernel.GetGoal(goal.Id);
        task = goal.FindTask(task.Id);
        var restored = Assert.Single(kernel.HumanInputRequests, item => item.Id == request.Id);
        Assert.Equal(2, restored.AnswerHistory.Count);
        Assert.True(restored.AnswerHistory[0].IsRetracted);
        Assert.Equal(replacement, restored.AuthoritativeAnswer!.Text);

        var next = fixture.Prepare(kernel, goal, task);

        fixture.AssertDelivered(next, goal, "replacement", nextSource, "replacement evidence");
        fixture.AssertAbsent(next, goal, "original");
        Assert.NotEqual(first.PromptPath, next.PromptPath);
        Assert.Equal(promptHash, WorkerStoreReferenceFixture.Hash(first.PromptPath));
        Assert.Equal(hashes, WorkerStoreReferenceFixture.Inventory(capture));
        Assert.Contains(receipt.Sections, item => item.LogicalIdentity == "context/store-refs/original.md");
        Assert.DoesNotContain(receipt.Sections, item => item.LogicalIdentity == "context/store-refs/replacement.md");
        Assert.Equal("original evidence", WorkerStoreReferenceFixture.Excerpt(
            File.ReadAllText(Path.Combine(capture, "store-refs", "original.md"))));
    }

    [Fact]
    public async Task UnrelatedBriefRevision_OlderAuthoritativeAnswer_RemainsDelivered()
    {
        using var fixture = new WorkerStoreReferenceFixture();
        var source = fixture.WriteSource("operator-evidence/older.md", "older authoritative evidence");
        var (kernel, goal, task) = fixture.CreateGoal("Implement original scope.");
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var request = fixture.Answer(kernel, goal, task.Id, "older");
        fixture.Advance();
        kernel.ReviseGoalBrief(goal.Id, "Implement scope with an unrelated wording change.");
        kernel = await fixture.Reload(kernel);
        goal = kernel.GetGoal(goal.Id);
        task = goal.FindTask(task.Id);
        Assert.Equal(1, Assert.Single(kernel.HumanInputRequests, item => item.Id == request.Id).AuthoritativeAnswer!.BriefVersion);
        Assert.Equal(2, goal.AuthoritativeBrief.Version);

        fixture.AssertDelivered(fixture.Prepare(kernel, goal, task), goal, "older", source, "older authoritative evidence");
    }
}
