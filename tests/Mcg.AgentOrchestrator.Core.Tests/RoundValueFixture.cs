using Mcg.AgentOrchestrator.Core;

internal static class RoundValueFixture
{
    internal static readonly DateTimeOffset Since = At("2026-09-24T00:00:00Z");
    internal static readonly DateTimeOffset Until = At("2026-09-26T00:00:00Z");

    internal static AgentOrchestratorKernel Create() => AgentOrchestratorKernel.FromSnapshot(
        new OrchestratorSnapshot(
        [
            new GoalSnapshot("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "A", GoalStatus.Completed,
            [
                Task("a-plan", AgentRole.Planner, WorkTaskStatus.Completed,
                    Dispatch("2026-09-24T01:00:00Z")),
                Task("a-dev", AgentRole.Developer, WorkTaskStatus.Completed,
                    Dispatch("2026-09-24T02:00:00Z", "c1", 1000, 400, 100),
                    Dispatch("2026-09-24T04:15:00Z", "c2")),
                Task("a-test", AgentRole.Tester, WorkTaskStatus.Completed,
                    Dispatch("2026-09-24T02:40:00Z"),
                    Dispatch("2026-09-24T03:00:00Z"), Dispatch("2026-09-24T03:00:00Z")),
                Task("a-rev", AgentRole.Reviewer, WorkTaskStatus.Completed,
                    Dispatch("2026-09-24T03:00:00Z", "c2"),
                    Dispatch("2026-09-24T05:00:00Z", "c3"), Dispatch("2026-09-24T06:00:00Z", "c3"))
            ],
            [
                Event('a', "a-plan", "2026-09-24T01:30:00Z", ProgressKind.TaskCompleted, "done"),
                Event('a', "a-dev", "2026-09-24T02:30:00Z", ProgressKind.TaskCompleted, "done"),
                Event('a', "a-dev", "2026-09-24T04:10:00Z", ProgressKind.TaskRetried, "auto-review-retry round 1: finding"),
                Event('a', "a-dev", "2026-09-24T04:45:00Z", ProgressKind.TaskCompleted, "done"),
                Event('a', "a-test", "2026-09-24T02:50:00Z", ProgressKind.TaskFailed, "inconclusive"),
                Event('a', "a-test", "2026-09-24T02:55:00Z", ProgressKind.TaskRetried, "Auto-retry verification-inconclusive Tester task"),
                Event('a', "a-test", "2026-09-24T03:20:00Z", ProgressKind.TaskCompleted, "done"),
                Event('a', "a-rev", "2026-09-24T03:30:00Z", ProgressKind.TaskFailed, "finding"),
                Event('a', "a-rev", "2026-09-24T04:50:00Z", ProgressKind.TaskRetried, "Invalidated after upstream Developer change"),
                Event('a', "a-rev", "2026-09-24T05:30:00Z", ProgressKind.TaskCompleted, "done"),
                Event('a', "a-rev", "2026-09-24T05:50:00Z", ProgressKind.TaskRetried, "Invalidated after upstream Developer change"),
                Event('a', "a-rev", "2026-09-24T06:30:00Z", ProgressKind.TaskCompleted, "done")
            ]),
            new GoalSnapshot("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "B", GoalStatus.Cancelled,
            [
                Task("b-dev", AgentRole.Developer, WorkTaskStatus.Completed,
                    Dispatch("2026-09-25T01:00:00Z", input: 500, cached: 200, output: 50),
                    Dispatch("2026-09-25T01:20:00Z"))
            ],
            [
                Event('b', "b-dev", "2026-09-25T01:10:00Z", ProgressKind.TaskFailed, "provider"),
                Event('b', "b-dev", "2026-09-25T01:15:00Z", ProgressKind.TaskRetried, "Dispatch hit provider connectivity failure"),
                Event('b', "b-dev", "2026-09-25T01:50:00Z", ProgressKind.TaskCompleted, "done")
            ]),
            new GoalSnapshot("cccccccccccccccccccccccccccccccc", "C", GoalStatus.Active,
            [
                Task("c-dev", AgentRole.Developer, WorkTaskStatus.Running,
                    Dispatch("2026-09-25T02:00:00Z"), Dispatch("2026-09-25T03:00:00Z"))
            ], []),
            new GoalSnapshot("dddddddddddddddddddddddddddddddd", "D", GoalStatus.Completed,
            [
                Task("d-plan", AgentRole.Planner, WorkTaskStatus.Completed, Dispatch("2026-09-25T05:00:00Z")),
                Task("d-dev", AgentRole.Developer, WorkTaskStatus.Completed, Dispatch("2026-09-26T01:00:00Z"))
            ],
            [
                Event('d', "d-plan", "2026-09-25T05:30:00Z", ProgressKind.TaskCompleted, "done"),
                Event('d', "d-dev", "2026-09-26T01:30:00Z", ProgressKind.TaskCompleted, "done")
            ])
        ], []));

    internal static DateTimeOffset At(string text) =>
        DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture);

    internal static TaskSnapshot Task(string id, AgentRole role, WorkTaskStatus status,
        params TaskDispatchSnapshot[] dispatches) =>
        new(id, id, role, status, null, null, null, [], null, null, DispatchHistory: dispatches);

    internal static TaskDispatchSnapshot Dispatch(string at, string? commit = null,
        long? input = null, long? cached = null, long? output = null) =>
        new("worker", "command", "root", At(at), BaseCommit: commit,
            ContextPackageReceipt: input is null && cached is null && output is null ? null :
                new WorkerContextPackageReceipt("package", [], Usage(input), Usage(cached), Usage(output)));

    private static ProviderUsageValue Usage(long? count) => count is { } value
        ? ProviderUsageValue.Reported(value) : ProviderUsageValue.Unknown("not reported");

    private static ProgressEventSnapshot Event(char goal, string task, string at, ProgressKind kind, string message) =>
        new(new string(goal, 32), task, kind, message, At(at));
}

