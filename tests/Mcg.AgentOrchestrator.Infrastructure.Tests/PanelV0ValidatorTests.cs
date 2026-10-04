using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class PanelV0ValidatorTests
{
    [Fact]
    public void Six_contract_cases_have_exact_typed_outcomes()
    {
        var valid = PanelTestHarness.Answer("case-a");
        var answers = new[] { "```json\n" + valid + "\n```", "Here is the answer: " + valid,
            valid.Replace("no-action", "invented-kind"), valid.Replace("case-a", "case-b"), "", valid };
        Assert.Equal(new[] { PanelJudgeOutcome.InvalidOutput, PanelJudgeOutcome.InvalidOutput,
            PanelJudgeOutcome.InvalidOutput, PanelJudgeOutcome.InvalidOutput, PanelJudgeOutcome.EmptyOutput, PanelJudgeOutcome.Valid },
            answers.Select(answer => PanelV0Contract.Validate(answer, "case-a")));
    }

    [Theory]
    [InlineData("extra")]
    [InlineData("duplicate")]
    [InlineData("two-objects")]
    [InlineData("array")]
    [InlineData("wrong-type")]
    public void Rejects_non_contract_shapes(string shape)
    {
        var valid = PanelTestHarness.Answer("case-a");
        var invalid = shape switch
        {
            "extra" => valid.Insert(1, "\"extra\":true,"),
            "duplicate" => valid.Insert(1, "\"schema\":\"panel-v0\","),
            "two-objects" => valid + valid, "array" => "[" + valid + "]",
            _ => valid.Replace("\"missing_evidence\":[]", "\"missing_evidence\":null")
        };
        Assert.Equal(PanelJudgeOutcome.InvalidOutput, PanelV0Contract.Validate(invalid, "case-a"));
    }
}
