using Mcg.AgentOrchestrator.Core;

public sealed class DogfoodLogRendererTests
{
    private static readonly DateTimeOffset FixedAt = new(2026, 6, 14, 10, 0, 0, TimeSpan.Zero);

    [Xunit.Fact(DisplayName = "DogfoodLogRenderer_renders_correct_entry_from_full_receipts")]
    public void DogfoodLogRendererRendersCorrectEntryFromFullReceipts()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var task = new TaskSpec(TaskId.New(), "Implement DogfoodLogRenderer", AgentRole.Developer);
        var goal = kernel.CreateGoal(
            "Add deterministic auto-recording for dogfood entries",
            [task]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());

        var dispatch = new TaskDispatchRecord(
            "claude-cli",
            "claude -p ...",
            "C:\\repo",
            clock.UtcNow,
            ProviderName: "Anthropic",
            ModelName: "claude-sonnet-4-6");
        kernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);

        var workerOutput = string.Join("\n",
            "Implemented the renderer and tests.",
            "WORKER_RESULT:",
            "files: src/DogfoodLogRenderer.cs",
            "commands: dotnet test",
            "tests: Core 50/50, Infrastructure 100/100",
            "commit: abc1234def",
            "blockers: none",
            "model_fit: Anthropic/claude-sonnet-4-6 - adequate - pure renderer implementation",
            "skills: orchestrator-dogfood",
            "confidence: high",
            "END_WORKER_RESULT");

        var verification = new TaskVerificationRecord(
            "dotnet test",
            "C:\\repo",
            0,
            workerOutput,
            string.Empty,
            clock.UtcNow);
        kernel.RecordTaskVerification(goal.Id, task.Id, verification);

        var entry = DogfoodLogRenderer.Render(goal);
        var text = entry.Render();

        Assert.True(text.Contains("## 2026-06-", StringComparison.Ordinal), "header must have date");
        Assert.True(text.Contains("Add deterministic auto-recording for dogfood entries", StringComparison.Ordinal), "header/summary must include objective");
        Assert.True(text.Contains("Anthropic/claude-sonnet-4-6", StringComparison.Ordinal), "summary must cite provider/model from dispatch");
        Assert.True(text.Contains("abc1234def", StringComparison.Ordinal), "summary must cite commit sha from WORKER_RESULT");
        Assert.True(text.Contains("exit 0", StringComparison.Ordinal), "summary must cite exit code from verification");
        Assert.True(text.Contains("Operator gate:", StringComparison.Ordinal), "entry must have Operator gate line");
        Assert.True(text.Contains("Core 50/50, Infrastructure 100/100", StringComparison.Ordinal), "operator gate must cite tests from WORKER_RESULT");
        Assert.True(text.Contains("Model fit:", StringComparison.Ordinal), "entry must have Model fit line");
        Assert.True(text.Contains("adequate", StringComparison.Ordinal), "model fit must cite fit rating from WORKER_RESULT");
        Assert.False(text.Contains("(no receipt)", StringComparison.Ordinal));
    }

    [Xunit.Fact(DisplayName = "DogfoodLogRenderer_emits_no_receipt_placeholders_when_receipts_are_absent")]
    public void DogfoodLogRendererEmitsNoReceiptPlaceholdersWhenReceiptsAreAbsent()
    {
        var kernel = new AgentOrchestratorKernel(new FakeClock());
        var task = new TaskSpec(TaskId.New(), "Do some work", AgentRole.Developer);
        var goal = kernel.CreateGoal("Feature with no dispatch receipt", [task]);

        var entry = DogfoodLogRenderer.Render(goal);
        var text = entry.Render();

        Assert.True(text.Contains("## ", StringComparison.Ordinal), "must have heading");
        Assert.True(text.Contains("Feature with no dispatch receipt", StringComparison.Ordinal), "must include objective");
        Assert.True(text.Contains("(no receipt)", StringComparison.Ordinal), "must emit placeholder for missing receipts");
        Assert.True(text.Contains("Operator gate:", StringComparison.Ordinal), "must have Operator gate line");
        Assert.True(text.Contains("Model fit:", StringComparison.Ordinal) || text.Contains("(no receipt)", StringComparison.Ordinal),
            "must have Model fit line or placeholder");
    }

    [Xunit.Fact(DisplayName = "DogfoodLogRenderer_uses_ModelFitNote_from_verification_record")]
    public void DogfoodLogRendererUsesModelFitNoteFromVerificationRecord()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var task = new TaskSpec(TaskId.New(), "Write tests", AgentRole.Tester);
        var goal = kernel.CreateGoal("Test model fit note extraction", [task]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());

        // Verification with pre-set ModelFitNote (no WORKER_RESULT block in output)
        var verification = new TaskVerificationRecord(
            "dotnet test",
            "C:\\repo",
            0,
            "All tests passed.",
            string.Empty,
            clock.UtcNow,
            ModelFitNote: "Model fit: OpenAI/gpt-5.5 - adequate - focused test suite");
        kernel.RecordTaskVerification(goal.Id, task.Id, verification);

        var entry = DogfoodLogRenderer.Render(goal);
        var text = entry.Render();

        Assert.True(text.Contains("Model fit: OpenAI/gpt-5.5 - adequate - focused test suite", StringComparison.Ordinal),
            "must use the pre-extracted ModelFitNote");
    }

    [Xunit.Fact(DisplayName = "DogfoodLogRenderer_TryParseWorkerResultField_extracts_commit_from_block")]
    public void DogfoodLogRendererTryParseWorkerResultFieldExtractsCommitFromBlock()
    {
        var verification = new TaskVerificationRecord(
            "cmd",
            "C:\\repo",
            0,
            "WORKER_RESULT:\nfiles: foo.cs\ncommands: dotnet build\ntests: 5/5\ncommit: deadbeef1234\nblockers: none\nmodel_fit: X/Y - adequate - z\nskills: none\nconfidence: high\nEND_WORKER_RESULT",
            string.Empty,
            DateTimeOffset.UtcNow);

        var commit = DogfoodLogRenderer.TryParseWorkerResultField(verification, "commit");
        Assert.Equal("deadbeef1234", commit);

        var tests = DogfoodLogRenderer.TryParseWorkerResultField(verification, "tests");
        Assert.Equal("5/5", tests);
    }

    [Xunit.Fact(DisplayName = "DogfoodLogRenderer_TryParseWorkerResultField_returns_null_when_block_absent")]
    public void DogfoodLogRendererTryParseWorkerResultFieldReturnsNullWhenBlockAbsent()
    {
        var verification = new TaskVerificationRecord(
            "cmd",
            "C:\\repo",
            0,
            "No WORKER_RESULT block here.",
            string.Empty,
            DateTimeOffset.UtcNow);

        Assert.Equal<string?>(null, DogfoodLogRenderer.TryParseWorkerResultField(verification, "commit"));
    }
}
