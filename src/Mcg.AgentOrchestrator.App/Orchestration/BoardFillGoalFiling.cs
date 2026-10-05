namespace Mcg.AgentOrchestrator.App.Orchestration;

// Intake owns goal creation; this seam only supplies the verb's arguments and receipts.
internal interface IBoardFillGoalIntake
{
    // Positive durable evidence only: a pending attempt alone does not authorize bypassing admission.
    string? FindCreatedGoal(string requestKey);
    BoardFillIntakeResult File(BoardFillIntakeRequest request, CancellationToken token);
    BoardFillIntakeResult Depend(string goalId, string dependencyGoalId, CancellationToken token);
}

internal sealed record BoardFillIntakeRequest(string DraftId, string BriefPath, string BacklogItemId)
{
    internal string RequestKey => "board-fill-" + DraftId;
    internal IReadOnlyList<string> Arguments => ["goal", "--brief-file", BriefPath,
        "--backlog-item", BacklogItemId, "--backlog-coverage", "full", "--request-key", RequestKey];
}

internal sealed record BoardFillIntakeResult(string Kind, string? GoalId, string Stdout, string Stderr,
    int ExitCode, string Reason = "ok");
internal sealed record BoardFillGoalBoard(int NonTerminalCount, IReadOnlySet<string> LinkedBacklogItemIds);
internal sealed record BoardFillFilingSeams(IBoardFillGoalIntake Intake, Func<BoardFillGoalBoard> Board,
    Func<string?> MainHead);
internal sealed record BoardFillDependencyResult(string GoalId, bool Applied, string Stdout, string Stderr, int ExitCode);
internal sealed record BoardFillFilingAttempt(string Id, string RequestKey, DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt = null, string? Result = null, string Reason = "pending", string? GoalId = null,
    string Stdout = "", string Stderr = "", int ExitCode = 0,
    IReadOnlyList<BoardFillDependencyResult>? Dependencies = null, bool Reported = false, bool Terminal = false,
    BoardFillIntakeResult? Intake = null);

internal static class BoardFillFiledEvent
{
    internal static string Id8(string id) => id[..Math.Min(8, id.Length)];
    internal static string Format(BoardFillDraftRound draft, BoardFillFilingAttempt attempt)
    {
        var depends = (attempt.Dependencies ?? []).Where(dependency => dependency.Applied)
            .Select(dependency => Id8(dependency.GoalId)).ToArray();
        return $"BOARD_FILL_FILED backlog={Id8(draft.BacklogItemId)} draft={draft.Id} result={attempt.Result} " +
            $"goal={(attempt.GoalId is null ? "none" : Id8(attempt.GoalId))} " +
            $"depends={(depends.Length == 0 ? "none" : string.Join(',', depends))} " +
            "reason=" + string.Join(' ', attempt.Reason.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
