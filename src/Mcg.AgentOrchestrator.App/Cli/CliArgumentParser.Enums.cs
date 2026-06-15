using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliArgumentParser
{
public static WorkTaskStatus ParseReportableStatus(string value)
{
    return value.ToLowerInvariant() switch
    {
        "running" => WorkTaskStatus.Running,
        "completed" => WorkTaskStatus.Completed,
        "failed" => WorkTaskStatus.Failed,
        "cancelled" => WorkTaskStatus.Cancelled,
        _ => throw new ArgumentException("Status must be running, completed, failed, or cancelled.")
    };
}

public static bool ParseManualVerificationPassed(string value)
{
    return value.ToLowerInvariant() switch
    {
        "passed" or "pass" => true,
        "failed" or "fail" => false,
        _ => throw new ArgumentException("Manual verification result must be passed or failed.")
    };
}

public static ProcessLogStream ParseProcessLogStream(string value)
{
    return value.ToLowerInvariant() switch
    {
        "all" => ProcessLogStream.All,
        "stdout" or "out" => ProcessLogStream.Stdout,
        "stderr" or "err" => ProcessLogStream.Stderr,
        "exit" or "exitcode" or "code" => ProcessLogStream.Exit,
        _ => throw new ArgumentException("Log stream must be stdout, stderr, exit, or all.")
    };
}

public static WorkTaskStatus ParseTaskQueryStatus(string value)
{
    return Enum.TryParse<WorkTaskStatus>(value, ignoreCase: true, out var status)
        ? status
        : throw new ArgumentException($"Status must be one of: {string.Join(", ", Enum.GetNames<WorkTaskStatus>())}.");
}

public static TaskEvidenceKind ParseTaskEvidenceKind(string value)
{
    return Enum.TryParse<TaskEvidenceKind>(NormalizeEnumToken(value), ignoreCase: true, out var evidence)
        ? evidence
        : throw new ArgumentException($"Evidence must be one of: {string.Join(", ", Enum.GetNames<TaskEvidenceKind>())}.");
}

public static ProgressKind ParseProgressKind(string value)
{
    return Enum.TryParse<ProgressKind>(NormalizeEnumToken(value), ignoreCase: true, out var kind)
        ? kind
        : throw new ArgumentException($"Event kind must be one of: {string.Join(", ", Enum.GetNames<ProgressKind>())}.");
}

public static string NormalizeEnumToken(string value)
{
    return value.Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal);
}

public static WorkerProfileImportMode ParseWorkerProfileImportMode(string value)
{
    return value.ToLowerInvariant() switch
    {
        "merge" => WorkerProfileImportMode.Merge,
        "replace" => WorkerProfileImportMode.Replace,
        _ => throw new ArgumentException("Worker profile import mode must be merge or replace.")
    };
}

public static AgentRole ParseAgentRole(string value)
{
    return Enum.TryParse<AgentRole>(value, ignoreCase: true, out var role)
        ? role
        : throw new ArgumentException($"Role must be one of: {string.Join(", ", Enum.GetNames<AgentRole>())}.");
}

public static ModelLane ParseModelLane(string value)
{
    return value.Trim().ToLowerInvariant() switch
    {
        "local" => ModelLane.Local,
        "cheap" or "cheap-api" or "cheapapi" => ModelLane.CheapApi,
        "capable" or "paid" => ModelLane.Capable,
        _ => throw new ArgumentException("Lane must be one of: local, cheap-api, capable.")
    };
}
}
