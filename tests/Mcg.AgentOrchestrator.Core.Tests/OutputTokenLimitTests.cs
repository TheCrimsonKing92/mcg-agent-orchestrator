using Mcg.AgentOrchestrator.Core;

public sealed class OutputTokenLimitTests
{
    [Xunit.Fact(DisplayName = "OutputTokenLimit_flags_truncation_stop_reasons")]
    public void OutputTokenLimitFlagsTruncationStopReasons()
    {
        Assert.True(OutputTokenLimit.IsHit(BuildExecution("length", outputTokens: 1, maxOutputTokens: 100)), "length must flag truncation");
        Assert.True(OutputTokenLimit.IsHit(BuildExecution("max_tokens", outputTokens: null, maxOutputTokens: null)), "max_tokens must flag truncation even without usage");
        Assert.True(OutputTokenLimit.IsHit(BuildExecution("incomplete", outputTokens: 1, maxOutputTokens: 100)), "incomplete must flag truncation");
    }

    [Xunit.Fact(DisplayName = "OutputTokenLimit_trusts_normal_stop_at_exact_cap")]
    public void OutputTokenLimitTrustsNormalStopAtExactCap()
    {
        Assert.False(OutputTokenLimit.IsHit(BuildExecution("end_turn", outputTokens: 100, maxOutputTokens: 100)));
        Assert.False(OutputTokenLimit.IsHit(BuildExecution("stop", outputTokens: 100, maxOutputTokens: 100)));
        Assert.False(OutputTokenLimit.IsHit(BuildExecution("completed", outputTokens: 100, maxOutputTokens: 100)));
    }

    [Xunit.Fact(DisplayName = "OutputTokenLimit_falls_back_to_token_count_for_unknown_stop_reason")]
    public void OutputTokenLimitFallsBackToTokenCountForUnknownStopReason()
    {
        Assert.True(OutputTokenLimit.IsHit(BuildExecution("unknown", outputTokens: 100, maxOutputTokens: 100)), "unknown stop reason at cap must flag truncation");
        Assert.False(OutputTokenLimit.IsHit(BuildExecution("unknown", outputTokens: 99, maxOutputTokens: 100)));
        Assert.False(OutputTokenLimit.IsHit(null));
    }

    private static TaskExecutionRecord BuildExecution(string stopReason, int? outputTokens, int? maxOutputTokens)
    {
        return new TaskExecutionRecord(
            AgentId.New(),
            "agent",
            "OpenAI",
            "gpt-test",
            "output",
            stopReason,
            outputTokens is null ? null : new ModelUsage(1, outputTokens),
            DateTimeOffset.UnixEpoch,
            MaxOutputTokens: maxOutputTokens);
    }
}
