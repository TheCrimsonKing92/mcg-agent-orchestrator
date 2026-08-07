using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum DispatchRecordWriteFailureCause
{
    Contention,
    Unrecoverable
}

internal sealed class DispatchRecordWriteException : InvalidOperationException
{
    internal DispatchRecordWriteException(
        DispatchRecordWriteFailureCause? cause,
        DispatchRecordCheckpointPhase checkpointPhase,
        string kind,
        GoalId goalId,
        TaskId taskId,
        int? sqliteErrorCode,
        Exception innerException,
        string? message = null)
        : base(message ?? FormatMessage(cause, checkpointPhase, kind, goalId, taskId, sqliteErrorCode, innerException), innerException)
    {
        Cause = cause;
        CheckpointPhase = checkpointPhase;
        Kind = kind;
        GoalId = goalId;
        TaskId = taskId;
        SqliteErrorCode = sqliteErrorCode;
    }

    internal DispatchRecordWriteFailureCause? Cause { get; }
    internal DispatchRecordCheckpointPhase CheckpointPhase { get; }
    internal string Kind { get; }
    internal GoalId GoalId { get; }
    internal TaskId TaskId { get; }
    internal int? SqliteErrorCode { get; }

    internal bool ProcessMayHaveStarted =>
        CheckpointPhase == DispatchRecordCheckpointPhase.ProcessMayHaveStarted;

    internal bool IsFatal =>
        ProcessMayHaveStarted || Cause == DispatchRecordWriteFailureCause.Unrecoverable;

    internal static DispatchRecordWriteException From(
        Exception exception,
        DispatchRecordCheckpointPhase checkpointPhase,
        string kind,
        GoalId goalId,
        TaskId taskId)
    {
        int? sqliteErrorCode = TryGetSqliteErrorCode(exception, out var code) ? code : null;
        DispatchRecordWriteFailureCause? cause = sqliteErrorCode is null
            ? null
            : ClassifySqliteErrorCode(sqliteErrorCode.Value);
        return new DispatchRecordWriteException(
            cause,
            checkpointPhase,
            kind,
            goalId,
            taskId,
            sqliteErrorCode,
            exception);
    }

    internal static DispatchRecordWriteFailureCause ClassifySqliteErrorCode(int sqliteErrorCode) =>
        sqliteErrorCode is 5 or 6
            ? DispatchRecordWriteFailureCause.Contention
            : DispatchRecordWriteFailureCause.Unrecoverable;

    internal static bool IsSqliteBusyOrLocked(Exception exception) =>
        TryGetSqliteErrorCode(exception, out var sqliteErrorCode) && sqliteErrorCode is 5 or 6;

    internal static bool TryGetSqliteErrorCode(Exception exception, out int sqliteErrorCode)
    {
        var current = exception;
        while (true)
        {
            var property = current.GetType().GetProperty("SqliteErrorCode");
            if (property?.GetValue(current) is int code)
            {
                sqliteErrorCode = code;
                return true;
            }

            if (current.InnerException is null)
            {
                sqliteErrorCode = 0;
                return false;
            }

            current = current.InnerException;
        }
    }

    private static string FormatMessage(
        DispatchRecordWriteFailureCause? cause,
        DispatchRecordCheckpointPhase checkpointPhase,
        string kind,
        GoalId goalId,
        TaskId taskId,
        int? sqliteErrorCode,
        Exception innerException)
    {
        var token = checkpointPhase == DispatchRecordCheckpointPhase.ProcessMayHaveStarted ||
            cause == DispatchRecordWriteFailureCause.Unrecoverable
                ? "DISPATCH_RECORD_WRITE_FAILED"
                : cause == DispatchRecordWriteFailureCause.Contention
                    ? "DISPATCH_RECORD_WRITE_CONTENTION"
                    : "DISPATCH_RECORD_WRITE_UNCLASSIFIED";
        var code = sqliteErrorCode?.ToString() ?? "unavailable";
        return $"{token} kind={kind} goal={Short(goalId.Value)} task={Short(taskId.Value)} " +
            $"checkpoint={checkpointPhase} sqliteCode={code} exception={innerException.GetType().FullName} " +
            $"error={ConductorBatchLoop.SanitizeReason(innerException.Message)}";
    }

    private static string Short(string value) => value.Length <= 8 ? value : value[..8];
}
