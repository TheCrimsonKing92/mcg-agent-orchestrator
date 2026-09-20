using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public enum DispatchRecoveryAction
{
    ReconcileFromExit,
    MarkStale,
    RetryStale,
    BudgetExhausted,
    Hold,
    ClassifyBlocker,
    Reap,
    PreserveInterruptedWork
}

public sealed record DispatchRecoveryDecision(
    DispatchRecoveryAction Action,
    string ActionName,
    string EvidencePath,
    string Reason,
    string? Blocker = null);

internal enum ExitCodeReadKind
{
    Missing,
    Unreadable,
    Invalid,
    Valid
}

internal sealed record ExitCodeReadResult(
    ExitCodeReadKind Kind,
    int? ExitCode,
    string Evidence,
    DispatchExitArtifactOrigin Origin = DispatchExitArtifactOrigin.None,
    string? Reason = null);

internal static class DispatchExitArtifactReader
{
    private const int EvidenceCharacterLimit = 256;

    public static ExitCodeReadResult Read(string path)
    {
        if (!File.Exists(path))
        {
            return new ExitCodeReadResult(ExitCodeReadKind.Missing, null, "file-missing");
        }

        try
        {
            if (DispatchExitArtifacts.TryRead(path, out var artifact))
            {
                var evidence = artifact.Origin == DispatchExitArtifactOrigin.UnknownLegacy
                    ? $"content={artifact.ExitCode}"
                    : $"origin={artifact.Origin} exit_code={artifact.ExitCode} reason={NormalizeEvidence(artifact.Reason)}";
                return new ExitCodeReadResult(
                    ExitCodeReadKind.Valid,
                    artifact.ExitCode,
                    evidence,
                    artifact.Origin,
                    artifact.Reason);
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var raw = reader.ReadToEnd();
            var evidenceText = raw[..Math.Min(raw.Length, EvidenceCharacterLimit)];
            var normalized = NormalizeEvidence(evidenceText);
            if (raw.Length > EvidenceCharacterLimit)
            {
                normalized += "...";
            }

            return new ExitCodeReadResult(
                ExitCodeReadKind.Invalid,
                null,
                $"content={normalized}; chars_read={raw.Length}");
        }
        catch (FileNotFoundException)
        {
            return new ExitCodeReadResult(ExitCodeReadKind.Missing, null, "file-missing");
        }
        catch (DirectoryNotFoundException)
        {
            return new ExitCodeReadResult(ExitCodeReadKind.Missing, null, "directory-missing");
        }
        catch (IOException ex)
        {
            return Unreadable(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unreadable(ex);
        }
    }

    private static ExitCodeReadResult Unreadable(Exception exception) =>
        new(
            ExitCodeReadKind.Unreadable,
            null,
            $"{exception.GetType().Name}: {NormalizeEvidence(exception.Message)}");

    private static string NormalizeEvidence(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "none";
        }

        var normalized = string.Join(
            " ",
            value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return string.IsNullOrWhiteSpace(normalized) ? "none" : normalized;
    }
}

public enum DispatchWorktreeInspectionAvailability
{
    NotRequired,
    Available,
    Unsafe,
    Unavailable
}

public sealed record DispatchWorktreeInspectionStatus(
    DispatchWorktreeInspectionAvailability Availability,
    bool HasDirtyEvidence,
    string EvidencePath,
    string? UnavailableReason = null,
    string? GitReceipt = null)
{
    public static DispatchWorktreeInspectionStatus NotRequired { get; } =
        new(DispatchWorktreeInspectionAvailability.NotRequired, false, "worktree-inspection-not-required");

    public static DispatchWorktreeInspectionStatus Available(bool hasDirtyEvidence, string evidencePath) =>
        new(DispatchWorktreeInspectionAvailability.Available, hasDirtyEvidence, evidencePath);

    public static DispatchWorktreeInspectionStatus Unavailable(
        string evidencePath,
        string reason,
        string gitReceipt) =>
        new(DispatchWorktreeInspectionAvailability.Unavailable, false, evidencePath, reason, gitReceipt);

    public static DispatchWorktreeInspectionStatus Unsafe(
        string evidencePath,
        string reason,
        string gitReceipt) =>
        new(DispatchWorktreeInspectionAvailability.Unsafe, true, evidencePath, reason, gitReceipt);
}

public sealed class DispatchRecoveryPolicy
{
    public static readonly TimeSpan DefaultRecentHeartbeatGrace = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DefaultLiveIdleTimeout = TimeSpan.FromMinutes(30);
    public const int DefaultStaleDispatchRetries = 1;
    public const int DefaultStaleDispatchAutoRequeues = 2;

    private readonly IClock _clock;
    private readonly TimeSpan _recentHeartbeatGrace;
    private readonly TimeSpan _liveIdleTimeout;

    public DispatchRecoveryPolicy(
        IClock? clock = null,
        TimeSpan? recentHeartbeatGrace = null,
        TimeSpan? liveIdleTimeout = null)
    {
        _clock = clock ?? new SystemClock();
        _recentHeartbeatGrace = recentHeartbeatGrace ?? DefaultRecentHeartbeatGrace;
        _liveIdleTimeout = liveIdleTimeout ?? DefaultLiveIdleTimeout;
    }

    public DispatchRecoveryDecision Evaluate(
        TaskProcessRecord process,
        bool hasLiveProcess,
        int staleRetryBudgetRemaining = 0,
        DispatchWorktreeInspectionStatus? worktreeInspection = null)
    {
        var exitPath = process.ExitCodePath;
        var heartbeat = ProcessLogReader.ReadHeartbeat(process, _clock.UtcNow);
        worktreeInspection ??= DispatchWorktreeInspectionStatus.NotRequired;

        var exitArtifact = DispatchExitArtifactReader.Read(exitPath);
        if (!hasLiveProcess && !heartbeat.IsAvailable && heartbeat.UnavailableReason is not "missing")
        {
            var unavailableReason = heartbeat.UnavailableReason ?? "unknown";
            return Decision(
                DispatchRecoveryAction.Hold,
                heartbeat.Path,
                $"heartbeat apparatus unavailable; unavailable_reason={unavailableReason}",
                $"heartbeat-{unavailableReason}");
        }

        if (!hasLiveProcess && worktreeInspection.Availability is
            DispatchWorktreeInspectionAvailability.Unavailable or
            DispatchWorktreeInspectionAvailability.Unsafe)
        {
            var inspectionState = worktreeInspection.Availability == DispatchWorktreeInspectionAvailability.Unsafe
                ? "unsafe"
                : "unavailable";
            return Decision(
                DispatchRecoveryAction.Hold,
                worktreeInspection.EvidencePath,
                $"worktree inspection {inspectionState}; unavailable_reason={worktreeInspection.UnavailableReason ?? "unknown"}; " +
                $"git_receipt={worktreeInspection.GitReceipt ?? "none"}",
                $"worktree-inspection-{inspectionState}");
        }

        if (!hasLiveProcess &&
            exitArtifact.Kind == ExitCodeReadKind.Valid &&
            exitArtifact.Origin == DispatchExitArtifactOrigin.Synthetic &&
            process.CompletedAt is null)
        {
            return Decision(
                DispatchRecoveryAction.PreserveInterruptedWork,
                exitPath,
                $"no live process and synthetic exit artifact exists; reason={exitArtifact.Reason}",
                "interrupted worker evidence requires operator verification");
        }

        if (!hasLiveProcess && exitArtifact.Kind == ExitCodeReadKind.Valid)
        {
            return Decision(
                DispatchRecoveryAction.ReconcileFromExit,
                exitPath,
                $"no live process and valid exit artifact exists; {exitArtifact.Evidence}");
        }

        if (!hasLiveProcess && exitArtifact.Kind is ExitCodeReadKind.Invalid or ExitCodeReadKind.Unreadable)
        {
            var artifactState = exitArtifact.Kind.ToString().ToLowerInvariant();
            return Decision(
                DispatchRecoveryAction.Hold,
                exitPath,
                $"exit artifact apparatus unavailable; state={exitArtifact.Kind}; evidence={exitArtifact.Evidence}",
                $"exit-artifact-{artifactState}");
        }

        if (!hasLiveProcess)
        {
            var heartbeatEvidence = heartbeat.IsAvailable ? heartbeat.Path : "heartbeat-absent";
            var heartbeatStale = !heartbeat.IsAvailable ||
                heartbeat.HeartbeatAge is null ||
                heartbeat.HeartbeatAge >= _recentHeartbeatGrace;
            if (heartbeatStale)
            {
                if (staleRetryBudgetRemaining > 0 && !worktreeInspection.HasDirtyEvidence)
                {
                    return Decision(
                        DispatchRecoveryAction.MarkStale,
                        heartbeatEvidence,
                        $"no live process, exit-absent, stale retry budget remaining={staleRetryBudgetRemaining}");
                }

                var blocker = staleRetryBudgetRemaining <= 0
                    ? "stale-dispatch retry budget exhausted"
                    : "stale-dispatch retry blocked by dirty worktree evidence";
                return Decision(
                    staleRetryBudgetRemaining <= 0 ? DispatchRecoveryAction.BudgetExhausted : DispatchRecoveryAction.MarkStale,
                    heartbeatEvidence,
                    $"no live process, exit-absent, heartbeat {(heartbeat.IsAvailable ? "stale" : $"unavailable reason={heartbeat.UnavailableReason ?? "unknown"}")}",
                    blocker);
            }

            return Decision(
                DispatchRecoveryAction.MarkStale,
                heartbeat.Path,
                "no live process, exit-absent, heartbeat present but process liveness is absent",
                "stale-dispatch process missing despite recent heartbeat");
        }

        if (heartbeat.IsAvailable)
        {
            if (heartbeat.ChildProcessId is not null &&
                heartbeat.HeartbeatAge <= _recentHeartbeatGrace)
            {
                return Decision(
                    DispatchRecoveryAction.Hold,
                    heartbeat.Path,
                    $"live process with recent heartbeat age={FormatDuration(heartbeat.HeartbeatAge.Value)}");
            }

            if (heartbeat.IdleDuration is { } idleDuration &&
                idleDuration < _liveIdleTimeout &&
                heartbeat.LastProgressAt is not null)
            {
                return Decision(
                    DispatchRecoveryAction.Hold,
                    heartbeat.Path,
                    $"live process with recent CPU activity or output progress idle_for={FormatDuration(idleDuration)} ownedCpuMs={heartbeat.OwnedCpuMs}");
            }

            if (heartbeat.IdleDuration >= _liveIdleTimeout &&
                heartbeat.StandardOutputBytes == 0 &&
                heartbeat.StandardErrorBytes == 0 &&
                SafeFileLength(process.StandardOutputPath) == 0 &&
                SafeFileLength(process.StandardErrorPath) == 0)
            {
                return Decision(
                    DispatchRecoveryAction.ClassifyBlocker,
                    heartbeat.Path,
                    $"live process idle for {FormatDuration(heartbeat.IdleDuration.Value)} with no heartbeat/output/CPU progress",
                    "live-idle-no-progress");
            }
        }

        return Decision(
            DispatchRecoveryAction.Hold,
            heartbeat.Path,
            heartbeat.IsAvailable
                ? "live process has no terminal artifact and has not exceeded live-idle policy"
                : $"live process heartbeat unavailable reason={heartbeat.UnavailableReason ?? "unknown"}; CPU sampling unavailable, holding pending other detectors");
    }

    public static string ToActionName(DispatchRecoveryAction action) =>
        action switch
        {
            DispatchRecoveryAction.ReconcileFromExit => "reconcile-from-exit",
            DispatchRecoveryAction.MarkStale => "mark-stale",
            DispatchRecoveryAction.RetryStale => "retry-stale",
            DispatchRecoveryAction.BudgetExhausted => "budget-exhausted",
            DispatchRecoveryAction.Hold => "hold",
            DispatchRecoveryAction.ClassifyBlocker => "classify-blocker",
            DispatchRecoveryAction.Reap => "reap",
            DispatchRecoveryAction.PreserveInterruptedWork => "preserve-interrupted-work",
            _ => action.ToString()
        };

    public static int GetStaleRetryBudgetRemaining(TaskSpec task)
    {
        var consumed = CountConsecutiveLegacyStaleDispatchRetries(task);
        return Math.Max(0, DefaultStaleDispatchRetries - consumed);
    }

    public static int GetStaleAutoRequeueBudgetRemaining(TaskSpec task)
    {
        var consumed = CountConsecutiveStaleDispatchAutoRequeues(task);
        return Math.Max(0, DefaultStaleDispatchAutoRequeues - consumed);
    }

    public static bool IsStaleDispatchRetryVerification(TaskVerificationRecord verification) =>
        verification.StandardError.Contains("Dispatch recovery policy action='retry-stale'", StringComparison.Ordinal) ||
        IsLegacyStaleDispatchRetryVerification(verification);

    private static bool IsLegacyStaleDispatchRetryVerification(TaskVerificationRecord verification) =>
        (verification.StandardError.Contains("Dispatch recovery policy action='mark-stale'", StringComparison.Ordinal) &&
         verification.StandardError.Contains("stale retry budget remaining=", StringComparison.Ordinal) &&
         !verification.StandardError.Contains("blocker='", StringComparison.Ordinal));

    public static bool IsStaleDispatchRecoveryVerification(TaskVerificationRecord verification) =>
        IsStaleDispatchRetryVerification(verification) ||
        verification.StandardError.Contains("Dispatch recovery policy action='mark-stale'", StringComparison.Ordinal) ||
        verification.StandardError.Contains("Dispatch recovery policy action='budget-exhausted'", StringComparison.Ordinal);

    private static int CountConsecutiveLegacyStaleDispatchRetries(TaskSpec task)
    {
        var consumed = 0;
        for (var i = task.VerificationHistory.Count - 1; i >= 0; i--)
        {
            if (!IsLegacyStaleDispatchRetryVerification(task.VerificationHistory[i]))
            {
                break;
            }

            consumed++;
        }

        return consumed;
    }

    private static int CountConsecutiveStaleDispatchAutoRequeues(TaskSpec task)
    {
        var consumed = 0;
        for (var i = task.VerificationHistory.Count - 1; i >= 0; i--)
        {
            if (!task.VerificationHistory[i].StandardError.Contains("Dispatch recovery policy action='retry-stale'", StringComparison.Ordinal))
            {
                break;
            }

            consumed++;
        }

        return consumed;
    }

    private static DispatchRecoveryDecision Decision(
        DispatchRecoveryAction action,
        string evidencePath,
        string reason,
        string? blocker = null) =>
        new(action, ToActionName(action), evidencePath, reason, blocker);

    private static long SafeFileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0L;
        }
        catch (IOException)
        {
            return 0L;
        }
        catch (UnauthorizedAccessException)
        {
            return 0L;
        }
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration < TimeSpan.Zero ? TimeSpan.Zero.ToString("c") : duration.ToString("c");
}
