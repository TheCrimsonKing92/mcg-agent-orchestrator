namespace Mcg.AgentOrchestrator.Core;

public static class OutputTokenLimit
{
    // Stop reasons that providers report when output was cut at the token cap:
    // chat-completions "length", Anthropic "max_tokens", OpenAI Responses "incomplete".
    private static readonly HashSet<string> TruncationStopReasons = new(StringComparer.OrdinalIgnoreCase)
    {
        "length", "max_tokens", "max_output_tokens", "incomplete"
    };

    // Stop reasons that mean the model finished on its own; output that lands
    // exactly on the cap with one of these is complete, not truncated.
    private static readonly HashSet<string> NormalStopReasons = new(StringComparer.OrdinalIgnoreCase)
    {
        "stop", "end_turn", "stop_sequence", "completed", "tool_use"
    };

    public static bool IsHit(TaskExecutionRecord? execution)
    {
        if (execution is null)
        {
            return false;
        }

        if (TruncationStopReasons.Contains(execution.StopReason))
        {
            return true;
        }

        if (NormalStopReasons.Contains(execution.StopReason))
        {
            return false;
        }

        return execution.MaxOutputTokens is > 0 &&
            execution.Usage?.OutputTokens is { } outputTokens &&
            outputTokens >= execution.MaxOutputTokens.Value;
    }
}
