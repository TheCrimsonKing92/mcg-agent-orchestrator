namespace Mcg.AgentOrchestrator.Core;

public enum InquiryExecutionMode
{
    Resume,
    ForkedResume,
    FreshExec
}

public enum InquiryAdmissionCheckKind
{
    SameGoal,
    SameTask,
    SameRole,
    SameProvider,
    SameWorktree,
    SessionPresent,
    SessionNotRetired,
    SpawnHeadAncestor,
    NoCriteriaCorrectionSinceCapture,
    NoIntegrationChangeSinceCapture,
    SessionNotConsumedByNonForkedInquiry,
    SessionAge,
    SessionTurnCount
}

public enum InquiryAdmissionCheckStatus
{
    Passed,
    Failed
}

public sealed record InquiryAdmissionCheck(
    InquiryAdmissionCheckKind Kind,
    InquiryAdmissionCheckStatus Status,
    string Reason)
{
    public bool Passed => Status == InquiryAdmissionCheckStatus.Passed;
}

public sealed record InquiryAdmissionOptions(TimeSpan MaxSessionAge, int MaxSessionTurns)
{
    public static InquiryAdmissionOptions Default { get; } = new(TimeSpan.FromDays(7), 8);
}

public sealed record InquiryAdmissionContext(
    GoalId RequestedGoalId,
    TaskId RequestedTaskId,
    AgentRole RequestedRole,
    ProviderKind RequestedProviderKind,
    string RequestedWorktree,
    string? CurrentWorktreeHeadSha,
    bool CapturedHeadIsAncestorOfCurrentHead,
    DateTimeOffset Now,
    DateTimeOffset? LatestCriteriaCorrectionAt,
    DateTimeOffset? LatestIntegrationChangeAt,
    bool SessionConsumedByNonForkedInquiry,
    int SessionTurnCount);

public sealed record InquiryAdmissionDecision(IReadOnlyList<InquiryAdmissionCheck> Checks)
{
    public bool AllowsResume => Checks.All(check => check.Passed);

    public string Outcome => AllowsResume ? "resume-admitted" : "fresh-exec-fallback";

    public IReadOnlyList<InquiryAdmissionCheck> FailedChecks =>
        Checks.Where(check => !check.Passed).ToArray();

    public string Reason => AllowsResume
        ? "all inquiry resume admission checks passed"
        : string.Join("; ", FailedChecks.Select(check => $"{check.Kind}: {check.Reason}"));
}

public static class InquiryResumeAdmission
{
    public static InquiryAdmissionDecision Evaluate(
        Goal parentGoal,
        TaskSpec parentTask,
        TaskDispatchRecord parentDispatch,
        InquiryAdmissionContext context,
        InquiryAdmissionOptions? options = null)
    {
        options ??= InquiryAdmissionOptions.Default;
        var checks = new List<InquiryAdmissionCheck>
        {
            Check(
                InquiryAdmissionCheckKind.SameGoal,
                parentGoal.Id == context.RequestedGoalId,
                $"parent goal {parentGoal.Id.Value} matches request",
                $"parent goal {parentGoal.Id.Value} does not match request {context.RequestedGoalId.Value}"),
            Check(
                InquiryAdmissionCheckKind.SameTask,
                parentTask.Id == context.RequestedTaskId,
                $"parent task {parentTask.Id.Value} matches request",
                $"parent task {parentTask.Id.Value} does not match request {context.RequestedTaskId.Value}"),
            Check(
                InquiryAdmissionCheckKind.SameRole,
                parentTask.RequiredRole == context.RequestedRole,
                $"parent role {parentTask.RequiredRole} matches request",
                $"parent role {parentTask.RequiredRole} does not match request {context.RequestedRole}"),
            Check(
                InquiryAdmissionCheckKind.SameProvider,
                parentDispatch.WorkerProviderKind == context.RequestedProviderKind,
                $"parent provider {parentDispatch.WorkerProviderKind} matches request",
                $"parent provider {parentDispatch.WorkerProviderKind} does not match request {context.RequestedProviderKind}"),
            Check(
                InquiryAdmissionCheckKind.SameWorktree,
                SamePath(parentDispatch.WorkingDirectory, context.RequestedWorktree),
                "parent worktree matches request",
                $"parent worktree '{parentDispatch.WorkingDirectory}' does not match request '{context.RequestedWorktree}'"),
            Check(
                InquiryAdmissionCheckKind.SessionPresent,
                !string.IsNullOrWhiteSpace(parentDispatch.ProviderSessionId),
                "parent dispatch captured provider session id",
                "parent dispatch has no provider session id"),
            Check(
                InquiryAdmissionCheckKind.SessionNotRetired,
                parentDispatch.ProviderSessionRetiredAt is null,
                "parent provider session is not retired",
                "parent provider session is retired"),
            CheckSpawnHead(parentDispatch, context),
            CheckNotAfter(
                InquiryAdmissionCheckKind.NoCriteriaCorrectionSinceCapture,
                context.LatestCriteriaCorrectionAt,
                parentDispatch.DispatchedAt,
                "no criteria correction recorded after parent dispatch",
                "criteria correction recorded after parent dispatch"),
            CheckNotAfter(
                InquiryAdmissionCheckKind.NoIntegrationChangeSinceCapture,
                context.LatestIntegrationChangeAt,
                parentDispatch.DispatchedAt,
                "no integration change recorded after parent dispatch",
                "integration change recorded after parent dispatch"),
            Check(
                InquiryAdmissionCheckKind.SessionNotConsumedByNonForkedInquiry,
                !context.SessionConsumedByNonForkedInquiry,
                "session has not been consumed by a prior non-forked inquiry",
                "session was consumed by a prior non-forked inquiry"),
            Check(
                InquiryAdmissionCheckKind.SessionAge,
                context.Now >= parentDispatch.DispatchedAt &&
                    context.Now - parentDispatch.DispatchedAt <= options.MaxSessionAge,
                $"session age is within {options.MaxSessionAge}",
                $"session age exceeds {options.MaxSessionAge}"),
            Check(
                InquiryAdmissionCheckKind.SessionTurnCount,
                context.SessionTurnCount >= 1 && context.SessionTurnCount <= options.MaxSessionTurns,
                $"session turn count {context.SessionTurnCount} is within {options.MaxSessionTurns}",
                $"session turn count {context.SessionTurnCount} exceeds {options.MaxSessionTurns}")
        };

        return new InquiryAdmissionDecision(checks);
    }

    private static InquiryAdmissionCheck CheckSpawnHead(TaskDispatchRecord parentDispatch, InquiryAdmissionContext context)
    {
        if (string.IsNullOrWhiteSpace(parentDispatch.WorktreeHeadSha))
        {
            return Fail(InquiryAdmissionCheckKind.SpawnHeadAncestor, "parent dispatch has no captured worktree HEAD");
        }

        if (string.IsNullOrWhiteSpace(context.CurrentWorktreeHeadSha))
        {
            return Fail(InquiryAdmissionCheckKind.SpawnHeadAncestor, "current worktree HEAD is unavailable");
        }

        return Check(
            InquiryAdmissionCheckKind.SpawnHeadAncestor,
            context.CapturedHeadIsAncestorOfCurrentHead,
            $"captured HEAD {parentDispatch.WorktreeHeadSha} is an ancestor of current HEAD {context.CurrentWorktreeHeadSha}",
            $"captured HEAD {parentDispatch.WorktreeHeadSha} is not an ancestor of current HEAD {context.CurrentWorktreeHeadSha}");
    }

    private static InquiryAdmissionCheck CheckNotAfter(
        InquiryAdmissionCheckKind kind,
        DateTimeOffset? candidate,
        DateTimeOffset cutoff,
        string passed,
        string failedPrefix)
    {
        return candidate is null || candidate <= cutoff
            ? Pass(kind, passed)
            : Fail(kind, $"{failedPrefix}: {candidate:u}");
    }

    private static InquiryAdmissionCheck Check(InquiryAdmissionCheckKind kind, bool passed, string passReason, string failReason) =>
        passed ? Pass(kind, passReason) : Fail(kind, failReason);

    private static InquiryAdmissionCheck Pass(InquiryAdmissionCheckKind kind, string reason) =>
        new(kind, InquiryAdmissionCheckStatus.Passed, reason);

    private static InquiryAdmissionCheck Fail(InquiryAdmissionCheckKind kind, string reason) =>
        new(kind, InquiryAdmissionCheckStatus.Failed, reason);

    private static bool SamePath(string left, string right)
    {
        try
        {
            left = Path.GetFullPath(left);
            right = Path.GetFullPath(right);
        }
        catch
        {
            left = left.Trim();
            right = right.Trim();
        }

        return string.Equals(left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record InquiryTokenUsage(int? InputTokens, int? CachedInputTokens, int? OutputTokens);

public sealed record InquiryAnswerReceipt(
    string Kind,
    string ClaimsLabel,
    string ReceiptId,
    string ParentDispatchId,
    GoalId GoalId,
    TaskId TaskId,
    string WorkerName,
    ProviderKind ProviderKind,
    InquiryExecutionMode ExecutionMode,
    string? ParentSessionId,
    string? ForkedSessionId,
    string Question,
    string AnswerTranscriptPath,
    InquiryTokenUsage Usage,
    long WallTimeMilliseconds,
    InquiryAdmissionDecision AdmissionGate,
    string Command,
    int ExitCode,
    DateTimeOffset CompletedAt)
{
    public const string InquiryKind = "Inquiry";
    public const string PostHocClaimsLabel = "POST-HOC CLAIMS";
}
