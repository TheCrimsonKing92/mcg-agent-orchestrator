internal static class CliGoalEventsStatusGoldenFixtures
{
    // Captured from the legacy CliCommandDispatcher on Windows with the fixed goal seed.
    internal const string Status =
        "\r\n" +
        "Goal abc10000aaaaaaaaaaaaaaaaaaaaaaaa\r\n" +
        "Objective: Golden query fixture\r\n" +
        "Status: Draft\r\n" +
        "Tasks:\r\n" +
        "  1. [Pending] Researcher: Research constraints, APIs, and integration risks (unassigned)\r\n" +
        "  2. [Pending] Planner: Clarify goal and decompose the SDLC plan (unassigned)\r\n" +
        "  3. [Pending] Developer: Implement the requested software changes (unassigned)\r\n" +
        "  4. [Pending] Tester: Verify behavior with automated and manual checks (unassigned)\r\n" +
        "  5. [Pending] Reviewer: Review results, risks, and remaining work (unassigned)\r\n" +
        "\r\n";

    internal const string GoalEvents = "{\"eventKind\":\"golden\"}\r\n";
}
