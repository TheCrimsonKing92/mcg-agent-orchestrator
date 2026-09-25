internal static class CliAttentionNextGoldenFixtures
{
    internal readonly record struct ErrorResult(int ExitCode, string Stdout, string Stderr);

    // Pre-routing writer-handler messages at d86e4b404, with Program's stderr formatting.
    internal static readonly ErrorResult UnknownAttentionGoal = new(1, string.Empty,
        "KeyNotFoundException: No goal found matching prefix 'missing'.\r\n");
    internal static readonly ErrorResult UnknownNextFullGoal = new(1, string.Empty,
        "KeyNotFoundException: Goal 'missing' was not found.\r\n");
    internal static readonly ErrorResult AmbiguousNextFullGoal = new(1, string.Empty,
        "InvalidOperationException: Goal prefix 'abc10000' is ambiguous.\r\n");

    // Captured from the normal attention handler with an empty collaboration store on Windows.
    internal const string EmptyAttention = "\r\nAttention queue: 0 open reach-up item(s)\r\n  none\r\n\r\n";

    internal const string EmptyAttentionHistory = "\r\nAttention queue: 0 reach-up item(s)\r\n  none\r\n\r\n";

    internal static string EmptyGoalAttention(string goalId) =>
        $"No open attention items for goal {goalId}.\r\n";

    internal const string NextFull =
        "\r\n" +
        "Goal diagnostics abc10000 Draft: Golden next fixture\r\n" +
        "Mode: bounded; skipped deep readiness, subscription prompt estimation, recovery planning, supervisor planning, inbox scan, and dispatch worktree git inspection.\r\n" +
        "Disposition: state='Idle' confidence='Medium' action='next' blockers='none' reason='no dispatch recovery action is currently required' fresh='2026-09-24 00:00:00Z'\r\n" +
        "Dispatches:\r\n" +
        "  none\r\n" +
        "Next actions:\r\n" +
        "  DelegatePendingTask: Researcher: Research constraints, APIs, and integration risks\r\n" +
        "  DelegatePendingTask: Planner: Clarify goal and decompose the SDLC plan\r\n" +
        "  DelegatePendingTask: Developer: Implement the requested software changes\r\n" +
        "Deeper commands:\r\n" +
        "  readiness abc10000\r\n" +
        "  evidence abc10000\r\n" +
        "  stages abc10000\r\n" +
        "  gates abc10000\r\n" +
        "  subscription-plan abc10000\r\n" +
        "  model-outcomes\r\n" +
        "  dispatch-value [--since <yyyy-mm-dd>]\r\n" +
        "  loop-health\r\n" +
        "  failure-triage abc10000\r\n" +
        "  goal-recovery abc10000\r\n" +
        "  operator-inbox abc10000\r\n" +
        "\r\n";
}
