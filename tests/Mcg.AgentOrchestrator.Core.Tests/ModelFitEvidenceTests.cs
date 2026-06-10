using Mcg.AgentOrchestrator.Core;

public sealed class ModelFitEvidenceTests
{
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

    [Xunit.Fact(DisplayName = "ModelFitEvidence_rejects_literal_template_echo")]
    public void ModelFitEvidenceRejectsLiteralTemplateEcho()
    {
        var observation = ModelFitEvidence.TryParseNote(
            ModelFitEvidence.BuildNoteTemplate("OpenAI/gpt-5.5"));

        Assert.True(observation is null, "template echo must not count as evidence");
    }

    [Xunit.Fact(DisplayName = "ModelFitEvidence_drops_placeholder_task_shape")]
    public void ModelFitEvidenceDropsPlaceholderTaskShape()
    {
        var observation = ModelFitEvidence.TryParseNote(
            "Model fit: OpenAI/gpt-5.5 - overkill - <task shape> - <short reason>");

        Assert.True(observation is not null, "expected note to parse");
        Assert.Equal("overkill", observation!.Fit);
        Assert.True(observation.TaskShape is null, "placeholder task shape must be dropped");
    }

    [Xunit.Fact(DisplayName = "ModelFitEvidence_treats_unrecognized_fit_as_unknown")]
    public void ModelFitEvidenceTreatsUnrecognizedFitAsUnknown()
    {
        var observation = ModelFitEvidence.TryParseNote(
            "Model fit: OpenAI/gpt-5.5 - excellent - docs update");

        Assert.True(observation is not null, "expected note to parse");
        Assert.Equal("unknown", observation!.Fit);
    }
}
