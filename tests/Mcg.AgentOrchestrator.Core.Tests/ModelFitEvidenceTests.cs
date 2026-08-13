using Mcg.AgentOrchestrator.Core;

public sealed class ModelFitEvidenceTests
{
    [Xunit.Fact(DisplayName = "FindLatestNote_and_FindNotes_prefer_ModelFitNote_field_over_stdout_line")]
    public void FindMethodsPreferModelFitNoteFieldOverStdoutLine()
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Field wins over stdout");
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        var task = goal.Tasks.First(t => t.RequiredRole == AgentRole.Developer);

        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
            "dotnet test",
            "C:\\repo",
            0,
            "Tests passed.\nModel fit: OpenAI/gpt-4 - overkill - this line must not win",
            string.Empty,
            clock.UtcNow,
            ModelFitNote: "Model fit: Anthropic/claude-sonnet-4-6 - adequate - field wins test"));

        Assert.Equal(
            "Model fit: Anthropic/claude-sonnet-4-6 - adequate - field wins test",
            ModelFitEvidence.FindLatestNote(task));

        var notes = ModelFitEvidence.FindNotes(task).ToList();
        Assert.Equal(1, notes.Count);
        Assert.Equal("Model fit: Anthropic/claude-sonnet-4-6 - adequate - field wins test", notes[0]);
    }

    [Xunit.Fact(DisplayName = "FindLatestNote_and_FindNotes_fall_back_to_stdout_scrape_when_ModelFitNote_is_null")]
    public void FindMethodsFallBackToStdoutScrapeWhenModelFitNoteIsNull()
    {
        var timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var verification = new TaskVerificationSnapshot(
            "dotnet test",
            "C:\\repo",
            0,
            "Tests passed.\nModel fit: Anthropic/claude-sonnet-4-6 - adequate - old data fallback",
            string.Empty,
            timestamp,
            ModelFitNote: null);

        var snapshot = new OrchestratorSnapshot(
            [new GoalSnapshot(
                "goal-1",
                "Fallback test goal",
                GoalStatus.Active,
                [new TaskSnapshot(
                    "task-1",
                    "Developer task",
                    AgentRole.Developer,
                    WorkTaskStatus.Completed,
                    null, null,
                    verification,
                    [verification],
                    null, null)],
                [])],
            []);

        var restoredKernel = AgentOrchestratorKernel.FromSnapshot(snapshot);
        var restoredTask = restoredKernel.Goals.Single().Tasks.Single();

        Assert.Equal<string?>(null, restoredTask.LastVerification!.ModelFitNote);

        Assert.Equal(
            "Model fit: Anthropic/claude-sonnet-4-6 - adequate - old data fallback",
            ModelFitEvidence.FindLatestNote(restoredTask));

        var notes = ModelFitEvidence.FindNotes(restoredTask).ToList();
        Assert.Equal(1, notes.Count);
        Assert.Equal("Model fit: Anthropic/claude-sonnet-4-6 - adequate - old data fallback", notes[0]);
    }


    [Xunit.Fact(DisplayName = "ModelFitEvidence_parses_complete_note")]
    public void ModelFitEvidenceParsesCompleteNote()
    {
        var observation = ModelFitEvidence.TryParseNote(
            "Model fit: Ollama/qwen3:8b - adequate - focused refactor - handled scoped change well.");

        Assert.True(observation is not null, "expected note to parse");
        Assert.Equal("Ollama", observation!.ProviderName);
        Assert.Equal("qwen3:8b", observation.ModelName);
        Assert.Equal("adequate", observation.Fit);
        Assert.Equal("focused refactor", observation.TaskShape);
    }

    [Xunit.Fact(DisplayName = "ModelFitEvidence_parses_markdown_decorated_note")]
    public void ModelFitEvidenceParsesMarkdownDecoratedNote()
    {
        // Exact shape emitted by a claude-cli worker in the 2026-06-10 dogfood run.
        var observation = ModelFitEvidence.TryParseNote(
            "**Model fit:** Anthropic/claude-haiku-4-5 — adequate — simple file creation task — straightforward I/O operation with no complexity.");

        Assert.True(observation is not null, "expected markdown-decorated note to parse");
        Assert.Equal("Anthropic", observation!.ProviderName);
        Assert.Equal("claude-haiku-4-5", observation.ModelName);
        Assert.Equal("adequate", observation.Fit);
        Assert.Equal("simple file creation task", observation.TaskShape);
    }

    [Xunit.Fact(DisplayName = "ModelFitEvidence_rejects_literal_template_echo")]
    public void ModelFitEvidenceRejectsLiteralTemplateEcho()
    {
        var observation = ModelFitEvidence.TryParseNote(
            ModelFitEvidence.BuildNoteTemplate("OpenAI/gpt-5.5")); // Deliberate fixture text pins historical/parser behavior independently of the live catalog.

        Assert.True(observation is null, "template echo must not count as evidence");
    }

    [Xunit.Fact(DisplayName = "ModelFitEvidence_drops_placeholder_task_shape")]
    public void ModelFitEvidenceDropsPlaceholderTaskShape()
    {
        var observation = ModelFitEvidence.TryParseNote(
            "Model fit: OpenAI/gpt-5.5 - overkill - <task shape> - <short reason>"); // Deliberate fixture text pins historical/parser behavior independently of the live catalog.

        Assert.True(observation is not null, "expected note to parse");
        Assert.Equal("overkill", observation!.Fit);
        Assert.True(observation.TaskShape is null, "placeholder task shape must be dropped");
    }

    [Xunit.Fact(DisplayName = "ModelFitEvidence_treats_unrecognized_fit_as_unknown")]
    public void ModelFitEvidenceTreatsUnrecognizedFitAsUnknown()
    {
        var observation = ModelFitEvidence.TryParseNote(
            "Model fit: OpenAI/gpt-5.5 - excellent - docs update"); // Deliberate fixture text pins historical/parser behavior independently of the live catalog.

        Assert.True(observation is not null, "expected note to parse");
        Assert.Equal("unknown", observation!.Fit);
    }

    [Xunit.Fact(DisplayName = "ModelFitEvidence_merges_provider_and_model_casing_variants")]
    public void ModelFitEvidenceMergesProviderAndModelCasingVariants()
    {
        string?[] notes =
        [
            "Model fit: Anthropic/claude-opus-5 - adequate - focused review",
            "Model fit: anthropic/CLAUDE-OPUS-5 - overkill - FOCUSED REVIEW",
            "Model fit: ANTHROPIC/claude-opus-5 - underpowered - integration repair"
        ];

        var firstRun = ModelFitEvidence.BuildSummary(notes);
        var secondRun = ModelFitEvidence.BuildSummary(notes);

        var summary = Assert.Single(firstRun);
        Assert.Equal("Anthropic", summary.ProviderName);
        Assert.Equal("claude-opus-5", summary.ModelName);
        Assert.Equal(3, summary.NoteCount);
        Assert.Equal(1, summary.AdequateCount);
        Assert.Equal(1, summary.OverkillCount);
        Assert.Equal(1, summary.UnderpoweredCount);
        Assert.Equal(0, summary.UnknownCount);
        Assert.Equal(["focused review", "integration repair"], summary.TaskShapes);

        var repeated = Assert.Single(secondRun);
        Assert.Equal(summary.ProviderName, repeated.ProviderName);
        Assert.Equal(summary.ModelName, repeated.ModelName);
        Assert.Equal(summary.NoteCount, repeated.NoteCount);
        Assert.Equal(summary.TaskShapes, repeated.TaskShapes);
    }
}
