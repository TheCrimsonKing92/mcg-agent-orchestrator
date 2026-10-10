namespace Mcg.AgentOrchestrator.Infrastructure;

// A snapshot of incomplete coverage when a local lane confirms its test failure.
internal sealed record AcceptanceLaneEarlyStop(
    string ConfirmingLane,
    IReadOnlyList<string> NotRun,
    IReadOnlyList<string> CancelledMidRun,
    IReadOnlyList<string> RemoteLeftToFinish)
{
    internal const string ReceiptName = "infrastructure lane early stop";

    internal int StoppedCount => NotRun.Count + CancelledMidRun.Count + RemoteLeftToFinish.Count;

    internal string Summary =>
        $"lane-early-stop confirming_lane={Quote(ConfirmingLane)} coverage=partial lanes_stopped={StoppedCount} " +
        $"not_run={List(NotRun)} cancelled_mid_run={List(CancelledMidRun)} remote_left_to_finish={List(RemoteLeftToFinish)}";

    internal AcceptanceCheckResult ToReceiptCheck() =>
        new(ReceiptName, false, 1, Summary, ResultSummary: Summary);

    private static string List(IReadOnlyList<string> lanes) => $"[{string.Join(",", lanes.Select(Quote))}]";
    private static string Quote(string lane) => $"\"{lane.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
}
