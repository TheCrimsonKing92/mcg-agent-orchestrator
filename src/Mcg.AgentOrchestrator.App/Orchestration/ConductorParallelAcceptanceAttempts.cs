using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum ConductorParallelAcceptanceAttemptOutcome
{
    Running,
    Passed,
    Failed,
    StaleCandidate,
    ProcessDied,
    CorruptArtifacts,
    Cancelled,
    BlockedBuildSlot,
    BlockedBuildLock,
    LaunchFailed,
    Faulted,
    Reconciled
}

internal enum ConductorEvidenceAttemptOutcome
{
    Passed,
    Failed,
    Superseded,
    Faulted,
    Cancelled,
    LaunchFailed,
    BlockedBuildSlot,
    BlockedBuildLock,
    CorruptArtifacts,
    Unknown
}

internal enum ConductorEvidenceSupersessionCause
{
    RetryInvalidated,
    CandidateChanged,
    FocusedRequestChanged,
    GoalMovedOn,
    CoordinatorReplacement
}

internal enum ConductorParallelAcceptanceAttemptDecisionKind
{
    Started,
    Running,
    Completed,
    TerminalWithoutRun
}

internal sealed record ConductorParallelAcceptanceAttempt(
    string AttemptId,
    string GoalId,
    string GoalPrefix,
    int SlotIndex,
    string? BranchHeadSha,
    string? MainHeadSha,
    DateTimeOffset StartedAt,
    DateTimeOffset LastHeartbeatAt,
    int OwnerProcessId,
    ConductorParallelAcceptanceAttemptOutcome Outcome,
    string StdoutPath,
    string StderrPath,
    string ExitCodePath,
    string HeartbeatPath,
    string ResultPath,
    string MetadataPath,
    string? ExecutionDirectory = null,
    string? PolicyName = null,
    IReadOnlyList<string>? ScopePaths = null,
    DateTimeOffset? CompletedAt = null,
    DateTimeOffset? ReconciledAt = null,
    string? Detail = null,
    int TransientFailureCount = 0,
    IReadOnlyList<string>? TestResultPaths = null,
    IReadOnlyList<string>? LeaseReceipts = null,
    int ReplayedLeaseReceiptCount = 0,
    string Kind = ConductorParallelAcceptanceAttemptCoordinator.GateDispatchKind,
    string? FocusedEvidenceRequest = null,
    int Ordinal = 0,
    long? MonotonicStartedTimestamp = null,
    long? MonotonicTimestampFrequency = null,
    string? ConductEventLogPath = null,
    string? SupersededBy = null,
    ConductorEvidenceSupersessionCause? SupersessionCause = null)
{
    public string CandidateKey => $"{GoalId}:{BranchHeadSha ?? "unknown-branch"}:{MainHeadSha ?? "unknown-main"}";
}

internal sealed record ConductorParallelAcceptanceAttemptDecision(
    ConductorParallelAcceptanceAttemptDecisionKind Kind,
    ConductorParallelAcceptanceAttempt Attempt,
    ConductorParallelAcceptanceRunResult? Run = null)
{
    public static ConductorParallelAcceptanceAttemptDecision Started(ConductorParallelAcceptanceAttempt attempt) =>
        new(ConductorParallelAcceptanceAttemptDecisionKind.Started, attempt);

    public static ConductorParallelAcceptanceAttemptDecision Running(ConductorParallelAcceptanceAttempt attempt) =>
        new(ConductorParallelAcceptanceAttemptDecisionKind.Running, attempt);

    public static ConductorParallelAcceptanceAttemptDecision Completed(
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceRunResult run) =>
        new(ConductorParallelAcceptanceAttemptDecisionKind.Completed, attempt, run);

    public static ConductorParallelAcceptanceAttemptDecision TerminalWithoutRun(
        ConductorParallelAcceptanceAttempt attempt) =>
        new(ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun, attempt);
}

internal sealed record ConductorParallelAcceptanceOwnedProcessLaunch(
    ConductorParallelAcceptanceAttempt Attempt,
    Action<int> ExecuteInCurrentProcess);

internal sealed record ConductorParallelAcceptanceOwnedProcessLaunchResult(int ProcessId);

internal delegate ConductorParallelAcceptanceRunResult ConductorParallelAcceptanceRunAcceptance(
    ConductorParallelAcceptanceCandidate candidate,
    ConductorAutonomyPolicy policy,
    DotnetBuildEnvironmentLease? stableSlotLease,
    CancellationToken cancellationToken);

internal delegate ConductorParallelAcceptanceRunResult? ConductorParallelAcceptanceTryRunPreSlot(
    ConductorParallelAcceptanceCandidate candidate,
    ConductorAutonomyPolicy policy);

internal sealed class ConductorParallelAcceptanceAttemptCompletionGateViolationException(string message)
    : InvalidOperationException(message);

internal sealed class ConductorParallelAcceptanceAttemptCompletionGateForTests
{
    internal const int Capacity = 2;

    private readonly object _gate = new();
    private readonly Dictionary<string, HeldAttempt> _heldAttempts = new(StringComparer.Ordinal);

    internal ConductorParallelAcceptanceAttemptCompletionGateForTests()
    {
        if (Capacity != ConductorBatchLoop.DefaultParallelAcceptanceCapacity)
        {
            throw new InvalidOperationException(
                $"The test completion gate capacity {Capacity} no longer matches the production parallel-acceptance capacity " +
                $"{ConductorBatchLoop.DefaultParallelAcceptanceCapacity}; widen the test gate deliberately with the production change.");
        }
    }

    internal int HeldCount
    {
        get
        {
            lock (_gate)
            {
                return _heldAttempts.Count;
            }
        }
    }

    internal Handle HoldForTests(ConductorParallelAcceptanceAttempt attempt, Action complete)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(complete);

        lock (_gate)
        {
            if (_heldAttempts.Count >= Capacity)
            {
                throw new ConductorParallelAcceptanceAttemptCompletionGateViolationException(
                    $"Test acceptance-attempt completion gate capacity {Capacity} is exhausted.");
            }

            if (_heldAttempts.ContainsKey(attempt.AttemptId))
            {
                throw new ConductorParallelAcceptanceAttemptCompletionGateViolationException(
                    $"Acceptance attempt '{attempt.AttemptId}' is already held by the test completion gate.");
            }

            _heldAttempts.Add(attempt.AttemptId, new HeldAttempt(attempt, complete));
            return new Handle(this, attempt);
        }
    }

    internal Handle RequiredHandleForTests(string goalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goalId);

        lock (_gate)
        {
            var match = _heldAttempts.Values.SingleOrDefault(held =>
                string.Equals(held.Attempt.GoalId, goalId, StringComparison.Ordinal));
            return match is null
                ? throw new InvalidOperationException($"No held acceptance attempt exists for goal '{goalId}'.")
                : new Handle(this, match.Attempt);
        }
    }

    internal void CompleteAllForTests()
    {
        List<Exception>? failures = null;
        while (true)
        {
            string? attemptId;
            lock (_gate)
            {
                attemptId = _heldAttempts.Keys.FirstOrDefault();
            }

            if (attemptId is null)
            {
                break;
            }

            try
            {
                CompleteForTests(attemptId);
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException("One or more held test acceptance attempts failed during cleanup.", failures);
        }
    }

    private void CompleteForTests(string attemptId)
    {
        HeldAttempt held;
        lock (_gate)
        {
            if (!_heldAttempts.Remove(attemptId, out held!))
            {
                throw new InvalidOperationException(
                    $"Acceptance attempt '{attemptId}' is not held or has already completed.");
            }
        }

        held.Complete();
    }

    private sealed record HeldAttempt(ConductorParallelAcceptanceAttempt Attempt, Action Complete);

    internal sealed class Handle
    {
        private readonly ConductorParallelAcceptanceAttemptCompletionGateForTests _owner;

        internal Handle(
            ConductorParallelAcceptanceAttemptCompletionGateForTests owner,
            ConductorParallelAcceptanceAttempt attempt)
        {
            _owner = owner;
            Attempt = attempt;
        }

        internal ConductorParallelAcceptanceAttempt Attempt { get; }

        internal void CompleteForTests() => _owner.CompleteForTests(Attempt.AttemptId);
    }
}

internal sealed class ConductorParallelAcceptanceAttemptCoordinator
{
    internal const string OwnedProcessSubcommandName = "__acceptance-gate-attempt";
    internal const string GateDispatchKind = "gate";
    internal const string PreReviewEvidenceDispatchKind = "pre-review-evidence";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly object MetadataWriteGate = new();
    private static readonly ConcurrentDictionary<int, Process> OwnedProcessDrains = new();
    private const int RetainedAttemptCountPerGoal = 20;
    private static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(15);

    private readonly string _rootDirectory;
    private readonly string? _executionDirectory;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeProvider _timeProvider;
    private readonly Func<int, bool> _isProcessAlive;
    private readonly Func<ConductorParallelAcceptanceOwnedProcessLaunch, ConductorParallelAcceptanceOwnedProcessLaunchResult> _launchOwnedProcess;
    private readonly bool _runInline;
    private readonly ConductorParallelAcceptanceTryRunPreSlot? _tryRunPreSlot;
    private readonly TimeSpan _heartbeatInterval;
    private readonly TimeSpan _recentHeartbeatGrace;
    private readonly Action<ConductorParallelAcceptanceAttempt, string>? _heartbeatWritten;
    private readonly ConductorParallelAcceptanceAttemptCompletionGateForTests? _attemptCompletionGateForTests;
    private readonly Func<ConductorParallelAcceptanceAttempt, ConductorParallelAcceptanceCandidate, DotnetBuildEnvironmentLease?> _acquireStableSlotLease;
    private readonly ConductEventLogWriter? _conductEventLogWriter;

    internal ConductorParallelAcceptanceAttemptCoordinator(
        string rootDirectory,
        string? executionDirectory = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<int, bool>? isProcessAlive = null,
        Func<ConductorParallelAcceptanceOwnedProcessLaunch, ConductorParallelAcceptanceOwnedProcessLaunchResult>? launchOwnedProcess = null,
        bool runInline = false,
        ConductorParallelAcceptanceTryRunPreSlot? tryRunPreSlot = null,
        TimeSpan? heartbeatInterval = null,
        TimeSpan? recentHeartbeatGrace = null,
        Action<ConductorParallelAcceptanceAttempt, string>? heartbeatWritten = null,
        ConductorParallelAcceptanceAttemptCompletionGateForTests? attemptCompletionGateForTests = null,
        Func<ConductorParallelAcceptanceAttempt, ConductorParallelAcceptanceCandidate, DotnetBuildEnvironmentLease?>? acquireStableSlotLease = null,
        ConductEventLogWriter? conductEventLogWriter = null,
        TimeProvider? timeProvider = null)
    {
        if (runInline && attemptCompletionGateForTests is not null)
        {
            throw new ArgumentException(
                "The test completion gate can only hold attempts on the existing non-inline path.",
                nameof(attemptCompletionGateForTests));
        }

        _rootDirectory = rootDirectory;
        _executionDirectory = executionDirectory;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _utcNow = utcNow ?? _timeProvider.GetUtcNow;
        _isProcessAlive = isProcessAlive ?? IsProcessAlive;
        _launchOwnedProcess = launchOwnedProcess ?? LaunchExternalOwnedProcess;
        _runInline = runInline;
        _tryRunPreSlot = tryRunPreSlot;
        _heartbeatInterval = heartbeatInterval ?? DefaultHeartbeatInterval;
        _recentHeartbeatGrace = recentHeartbeatGrace ?? DispatchRecoveryPolicy.DefaultRecentHeartbeatGrace;
        _heartbeatWritten = heartbeatWritten;
        _attemptCompletionGateForTests = attemptCompletionGateForTests;
        _acquireStableSlotLease = acquireStableSlotLease ?? AcquireAttemptStableSlotLease;
        _conductEventLogWriter = conductEventLogWriter;
    }

    internal ConductorParallelAcceptanceAttemptDecision Evaluate(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        Func<ConductorParallelAcceptanceCandidate, ConductorAutonomyPolicy, ConductorParallelAcceptanceRunResult> runAcceptance) =>
        Evaluate(candidate, policy, (attemptCandidate, attemptPolicy, _, _) => runAcceptance(attemptCandidate, attemptPolicy));

    internal ConductorParallelAcceptanceAttemptDecision Evaluate(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        ConductorParallelAcceptanceRunAcceptance runAcceptance)
        => EvaluateCore(candidate, policy, runAcceptance, GateDispatchKind, focusedEvidenceRequest: null);

    internal ConductorParallelAcceptanceAttemptDecision EvaluateFocusedEvidence(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        string request,
        Func<Goal, string, DotnetBuildEnvironmentLease?, CancellationToken, FocusedEvidenceRunResult> runFocusedEvidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request);
        ArgumentNullException.ThrowIfNull(runFocusedEvidence);
        return EvaluateCore(
            candidate,
            policy,
            (attemptCandidate, _, lease, cancellationToken) => ConductorParallelAcceptanceRunResult.Focused(
                attemptCandidate,
                runFocusedEvidence(attemptCandidate.Goal, request, lease, cancellationToken)),
            PreReviewEvidenceDispatchKind,
            request);
    }

    private ConductorParallelAcceptanceAttemptDecision EvaluateCore(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        ConductorParallelAcceptanceRunAcceptance runAcceptance,
        string dispatchKind,
        string? focusedEvidenceRequest)
    {
        var current = TryReadLatest(candidate.Goal.Id.Value);
        if (current is not null && IsLiveInvalidatedAttempt(current))
        {
            return ConductorParallelAcceptanceAttemptDecision.Running(current);
        }

        if (current is not null && IsReconciled(current))
        {
            if (current.Outcome == ConductorParallelAcceptanceAttemptOutcome.StaleCandidate &&
                current.SupersessionCause is not null)
            {
                var successor = CreateAttempt(candidate, policy, dispatchKind, focusedEvidenceRequest);
                CompleteSupersession(current, successor);
                return Launch(candidate, policy, runAcceptance, dispatchKind, focusedEvidenceRequest, successor);
            }

            current = null;
        }

        if (current is not null && IsTerminalWithoutRunOutcome(current.Outcome))
        {
            if (!MatchesCandidate(current, candidate, dispatchKind, focusedEvidenceRequest))
            {
                return ReplaceStaleAttempt(
                    current,
                    candidate,
                    policy,
                    runAcceptance,
                    dispatchKind,
                    focusedEvidenceRequest);
            }

            return ConductorParallelAcceptanceAttemptDecision.TerminalWithoutRun(current);
        }

        if (current is not null && !IsReconciled(current))
        {
            var terminal = TryCompleteRunningAttempt(current, candidate);
            if (terminal is { Run: not null })
            {
                if (!MatchesCandidate(terminal.Attempt, candidate, dispatchKind, focusedEvidenceRequest))
                {
                    return ReplaceStaleAttempt(
                        terminal.Attempt,
                        candidate,
                        policy,
                        runAcceptance,
                        dispatchKind,
                        focusedEvidenceRequest);
                }

                return terminal;
            }

            if (terminal is not null)
            {
                if (!MatchesCandidate(terminal.Attempt, candidate, dispatchKind, focusedEvidenceRequest))
                {
                    return ReplaceStaleAttempt(
                        terminal.Attempt,
                        candidate,
                        policy,
                        runAcceptance,
                        dispatchKind,
                        focusedEvidenceRequest);
                }

                return terminal;
            }

            return ConductorParallelAcceptanceAttemptDecision.Running(current);
        }

        return Launch(candidate, policy, runAcceptance, dispatchKind, focusedEvidenceRequest);
    }

    private static bool MatchesCandidate(
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceCandidate candidate,
        string dispatchKind,
        string? focusedEvidenceRequest) =>
        string.Equals(attempt.CandidateKey, candidate.CandidateKey, StringComparison.Ordinal) &&
        string.Equals(
            string.IsNullOrWhiteSpace(attempt.Kind) ? GateDispatchKind : attempt.Kind,
            dispatchKind,
            StringComparison.Ordinal) &&
        string.Equals(attempt.FocusedEvidenceRequest, focusedEvidenceRequest, StringComparison.Ordinal);

    private ConductorParallelAcceptanceAttemptDecision ReplaceStaleAttempt(
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        ConductorParallelAcceptanceRunAcceptance runAcceptance,
        string dispatchKind,
        string? focusedEvidenceRequest)
    {
        var cause = SupersessionCauseFor(attempt, candidate, dispatchKind, focusedEvidenceRequest);
        MarkStale(attempt, cause);
        if (!string.Equals(dispatchKind, PreReviewEvidenceDispatchKind, StringComparison.Ordinal))
        {
            return Launch(candidate, policy, runAcceptance, dispatchKind, focusedEvidenceRequest);
        }

        var successor = CreateAttempt(candidate, policy, dispatchKind, focusedEvidenceRequest);
        CompleteSupersession(attempt, successor);
        return Launch(candidate, policy, runAcceptance, dispatchKind, focusedEvidenceRequest, successor);
    }

    private static ConductorEvidenceSupersessionCause SupersessionCauseFor(
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceCandidate candidate,
        string dispatchKind,
        string? focusedEvidenceRequest)
    {
        if (!string.Equals(attempt.CandidateKey, candidate.CandidateKey, StringComparison.Ordinal))
        {
            return ConductorEvidenceSupersessionCause.CandidateChanged;
        }

        if (!string.Equals(attempt.FocusedEvidenceRequest, focusedEvidenceRequest, StringComparison.Ordinal))
        {
            return ConductorEvidenceSupersessionCause.FocusedRequestChanged;
        }

        return !string.Equals(attempt.Kind, dispatchKind, StringComparison.Ordinal)
            ? ConductorEvidenceSupersessionCause.GoalMovedOn
            : ConductorEvidenceSupersessionCause.CoordinatorReplacement;
    }

    internal void MarkReconciled(ConductorParallelAcceptanceAttempt attempt)
    {
        lock (MetadataWriteGate)
        {
            var current = TryReadAttemptFile(attempt.MetadataPath) ?? attempt;
            if (current.ReconciledAt.HasValue)
            {
                return;
            }

            WriteAttemptFile(current with
            {
                ReconciledAt = _utcNow(),
                LastHeartbeatAt = _utcNow()
            });
        }
    }

    internal bool InvalidateCurrent(string goalId, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        lock (MetadataWriteGate)
        {
            var current = TryReadLatest(goalId);
            if (current is null || IsReconciled(current))
            {
                return false;
            }

            return MarkStaleUnderLock(
                current,
                reason.Trim(),
                ConductorEvidenceSupersessionCause.RetryInvalidated);
        }
    }

    internal bool TryGetLiveInvalidatedAttempt(
        string goalId,
        out ConductorParallelAcceptanceAttempt attempt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goalId);

        var current = TryReadLatest(goalId);
        if (current is not null && IsLiveInvalidatedAttempt(current))
        {
            attempt = current;
            return true;
        }

        attempt = null!;
        return false;
    }

    internal bool HasLiveAttempt(string goalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goalId);

        var attempt = TryReadLatest(goalId);
        return attempt is not null && IsLiveAttempt(attempt);
    }

    internal IReadOnlySet<string> GetLiveAttemptGoalIds(IEnumerable<string> goalIds)
    {
        ArgumentNullException.ThrowIfNull(goalIds);

        var liveGoalIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var goalId in goalIds.Distinct(StringComparer.Ordinal))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(goalId);
            var attempt = TryReadLatest(goalId);
            if (attempt is not null && IsLiveAttempt(attempt))
            {
                liveGoalIds.Add(goalId);
            }
        }

        return liveGoalIds;
    }

    internal string DescribeFocusedEvidenceHold(ConductorParallelAcceptanceAttempt attempt)
    {
        var elapsed = DurationSeconds(attempt);
        var humanElapsed = elapsed is double seconds
            ? FormatElapsed(TimeSpan.FromSeconds(seconds))
            : "unknown";
        return $"PRE_REVIEW_FOCUSED_EVIDENCE_RUNNING: attempt {attempt.Ordinal}, {humanElapsed} elapsed (attempt={attempt.AttemptId})";
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        var wholeSeconds = Math.Max(0, (long)elapsed.TotalSeconds);
        var hours = wholeSeconds / 3600;
        var minutes = wholeSeconds % 3600 / 60;
        var seconds = wholeSeconds % 60;
        return hours > 0
            ? $"{hours}h{minutes}m{seconds}s"
            : $"{minutes}m{seconds}s";
    }

    private bool IsLiveAttempt(ConductorParallelAcceptanceAttempt attempt) =>
        attempt.Outcome == ConductorParallelAcceptanceAttemptOutcome.Running &&
        !attempt.ReconciledAt.HasValue &&
        !File.Exists(attempt.ResultPath) &&
        !File.Exists(attempt.ExitCodePath) &&
        _isProcessAlive(attempt.OwnerProcessId) &&
        !IsHeartbeatStale(attempt);

    private bool IsLiveInvalidatedAttempt(ConductorParallelAcceptanceAttempt attempt) =>
        attempt.Outcome == ConductorParallelAcceptanceAttemptOutcome.StaleCandidate &&
        attempt.ReconciledAt.HasValue &&
        !File.Exists(attempt.ExitCodePath) &&
        _isProcessAlive(attempt.OwnerProcessId) &&
        !IsHeartbeatStale(attempt);

    internal IReadOnlyList<string> TakePendingLeaseReceipts(ConductorParallelAcceptanceAttempt attempt)
    {
        lock (MetadataWriteGate)
        {
            var current = TryReadAttemptFile(attempt.MetadataPath) ?? attempt;
            var receipts = current.LeaseReceipts ?? [];
            var replayedCount = Math.Clamp(current.ReplayedLeaseReceiptCount, 0, receipts.Count);
            if (replayedCount >= receipts.Count)
            {
                return [];
            }

            var pending = receipts.Skip(replayedCount).ToArray();
            WriteAttemptFile(current with
            {
                ReplayedLeaseReceiptCount = receipts.Count,
                LastHeartbeatAt = _utcNow()
            });
            return pending;
        }
    }

    private ConductorParallelAcceptanceAttemptDecision Launch(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        ConductorParallelAcceptanceRunAcceptance runAcceptance,
        string dispatchKind,
        string? focusedEvidenceRequest,
        ConductorParallelAcceptanceAttempt? reservedAttempt = null)
    {
        var attempt = reservedAttempt ?? CreateAttempt(candidate, policy, dispatchKind, focusedEvidenceRequest);
        try
        {
            Persist(attempt);
            EmitEvidenceStart(attempt);
            WriteHeartbeat(attempt, "starting");
            File.AppendAllText(attempt.StdoutPath, $"{attempt.Kind} attempt {attempt.AttemptId} started for {attempt.GoalPrefix} slot-{attempt.SlotIndex}{Environment.NewLine}");

            if (_runInline)
            {
                RunAttempt(attempt, candidate, policy, runAcceptance);
                var completed = TryReadLatest(candidate.Goal.Id.Value) ?? attempt;
                return TryCompleteRunningAttempt(completed, candidate)
                    ?? ConductorParallelAcceptanceAttemptDecision.TerminalWithoutRun(completed);
            }

            if (_attemptCompletionGateForTests is not null)
            {
                var held = TryPersistOwnerProcess(attempt, Environment.ProcessId);
                if (held.Outcome == ConductorParallelAcceptanceAttemptOutcome.Running)
                {
                    WriteHeartbeat(held, "running");
                }

                _attemptCompletionGateForTests.HoldForTests(
                    held,
                    () => RunAttempt(held, candidate, policy, runAcceptance));
                return ConductorParallelAcceptanceAttemptDecision.Started(held);
            }

            var launch = _launchOwnedProcess(new ConductorParallelAcceptanceOwnedProcessLaunch(
                attempt,
                childPid =>
                {
                    var activeAttempt = TryPersistOwnerProcess(attempt, childPid);
                    if (activeAttempt.Outcome != ConductorParallelAcceptanceAttemptOutcome.Running)
                    {
                        return;
                    }

                    RunAttempt(activeAttempt, candidate, policy, runAcceptance);
                }));
            var launched = TryPersistOwnerProcess(attempt, launch.ProcessId);
            if (launched.Outcome == ConductorParallelAcceptanceAttemptOutcome.Running)
            {
                WriteHeartbeat(launched, "running");
            }

            return ConductorParallelAcceptanceAttemptDecision.Started(launched);
        }
        catch (ConductorParallelAcceptanceAttemptCompletionGateViolationException ex)
        {
            var rejected = attempt with
            {
                Outcome = ConductorParallelAcceptanceAttemptOutcome.LaunchFailed,
                CompletedAt = _utcNow(),
                LastHeartbeatAt = _utcNow(),
                Detail = ex.Message
            };
            Persist(rejected);
            TryAppend(attempt.StderrPath, $"test completion gate rejected attempt: {ex.Message}{Environment.NewLine}");
            TryWriteExit(attempt.ExitCodePath, 1);
            throw;
        }
        catch (Exception ex)
        {
            var transientFailureCount = IsTransientAttemptIo(ex)
                ? CountConsecutiveTransientFailures(attempt) + 1
                : 0;
            var failed = attempt with
            {
                Outcome = ConductorParallelAcceptanceAttemptOutcome.LaunchFailed,
                CompletedAt = _utcNow(),
                Detail = ex.Message,
                TransientFailureCount = transientFailureCount
            };
            Persist(failed);
            TryAppend(attempt.StderrPath, $"launch failed: {ex}{Environment.NewLine}");
            TryWriteExit(attempt.ExitCodePath, 1);
            return ConductorParallelAcceptanceAttemptDecision.TerminalWithoutRun(failed);
        }
    }

    internal void RunAttemptForTests(
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        Func<ConductorParallelAcceptanceCandidate, ConductorAutonomyPolicy, ConductorParallelAcceptanceRunResult> runAcceptance) =>
        RunAttemptForTests(attempt, candidate, policy, (attemptCandidate, attemptPolicy, _, _) => runAcceptance(attemptCandidate, attemptPolicy));

    internal void RunAttemptForTests(
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        ConductorParallelAcceptanceRunAcceptance runAcceptance)
    {
        RunAttempt(attempt, candidate, policy, runAcceptance);
    }

    internal static int RunOwnedProcess(string metadataPath)
    {
        ConductorParallelAcceptanceAttempt? attempt = null;
        try
        {
            attempt = JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(
                ReadAllTextSharedWithRetry(metadataPath),
                JsonOptions);
            if (attempt is null)
            {
                throw new InvalidOperationException("acceptance attempt metadata was empty");
            }

            RedirectConsole(attempt);
            var executionDirectory = !string.IsNullOrWhiteSpace(attempt.ExecutionDirectory)
                ? attempt.ExecutionDirectory!
                : OrchestratorWorkspace.ResolveRepoRoot(Environment.CurrentDirectory);
            var workspace = OrchestratorWorkspace.ForDirectory(executionDirectory, executionDirectory);
            var stateRepository = SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath);
            var kernel = stateRepository.LoadGoalsAsync([new GoalId(attempt.GoalId)]).GetAwaiter().GetResult();
            var goal = kernel.Goals.FirstOrDefault(g => g.Id.Value == attempt.GoalId)
                ?? throw new InvalidOperationException($"goal {attempt.GoalPrefix} was not found for acceptance attempt");
            var providers = ProviderRegistryFactory.CreateDefaultProviders();
            var agentFallback = ProviderRegistryFactory.IsOllamaReachable() ? AgentCatalog.OllamaDefault() : null;
            var agents = AgentCatalogStore.Load(workspace.AgentCatalogPath, agentFallback).Agents;
            var profiles = WorkerProfileStore.Load(workspace.WorkerProfilePath);
            var driver = new ConductorDriver(
                kernel,
                workspace,
                new GoalAcceptanceVerifier(),
                agents,
                profiles,
                NullOperatorChannel.Instance,
                providers);
            var policy = ConductorAutonomyPolicy.All.FirstOrDefault(candidatePolicy =>
                    string.Equals(candidatePolicy.Name, attempt.PolicyName, StringComparison.OrdinalIgnoreCase))
                ?? ConductorAutonomyPolicy.Default;
            var candidate = ConductorParallelAcceptanceCandidate.Create(
                goal,
                attempt.SlotIndex,
                attempt.ScopePaths ?? [],
                attempt.BranchHeadSha,
                attempt.MainHeadSha);
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.GetDirectoryName(Path.GetDirectoryName(attempt.MetadataPath) ?? string.Empty) ?? executionDirectory,
                executionDirectory,
                tryRunPreSlot: driver.RunParallelLandingAcceptancePreSlot,
                conductEventLogWriter: string.IsNullOrWhiteSpace(attempt.ConductEventLogPath)
                    ? null
                    : new ConductEventLogWriter(attempt.ConductEventLogPath));
            var activeAttempt = coordinator.TryPersistOwnerProcess(attempt, Environment.ProcessId);
            if (activeAttempt.Outcome != ConductorParallelAcceptanceAttemptOutcome.Running)
            {
                return 0;
            }

            if (string.Equals(activeAttempt.Kind, PreReviewEvidenceDispatchKind, StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(activeAttempt.FocusedEvidenceRequest))
                {
                    throw new InvalidOperationException("pre-review evidence attempt metadata did not contain a request");
                }

                coordinator.RunAttempt(
                    activeAttempt,
                    candidate,
                    policy,
                    (attemptCandidate, _, lease, cancellationToken) =>
                        driver.RunPreReviewFocusedEvidence(
                            attemptCandidate,
                            activeAttempt.FocusedEvidenceRequest,
                            lease,
                            cancellationToken));
            }
            else
            {
                coordinator.RunAttempt(
                    activeAttempt,
                    candidate,
                    policy,
                    driver.RunParallelLandingAcceptance);
            }
            return 0;
        }
        catch (Exception ex)
        {
            if (attempt is not null)
            {
                TryAppend(attempt.StderrPath, $"{ex}{Environment.NewLine}");
                TryWriteExit(attempt.ExitCodePath, 1);
                var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                    Path.GetDirectoryName(Path.GetDirectoryName(attempt.MetadataPath) ?? string.Empty) ?? Environment.CurrentDirectory,
                    attempt.ExecutionDirectory,
                    conductEventLogWriter: string.IsNullOrWhiteSpace(attempt.ConductEventLogPath)
                        ? null
                        : new ConductEventLogWriter(attempt.ConductEventLogPath));
                coordinator.CompleteWithoutResult(
                    attempt with { OwnerProcessId = Environment.ProcessId },
                    IsTransientAttemptIo(ex)
                        ? ConductorParallelAcceptanceAttemptOutcome.CorruptArtifacts
                        : ConductorParallelAcceptanceAttemptOutcome.Failed,
                    ex.Message,
                    transient: IsTransientAttemptIo(ex));
            }
            else
            {
                Console.Error.WriteLine(ex);
            }

            return 1;
        }
    }

    private void RunAttempt(
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        ConductorParallelAcceptanceRunAcceptance runAcceptance)
    {
        DotnetBuildEnvironmentLease? stableSlotLease = null;
        ConductorParallelAcceptanceRunResult? run = null;
        (ConductorParallelAcceptanceAttemptOutcome Outcome, string Detail, bool Transient)? terminalWithoutResult = null;
        string? stderrDetail = null;
        using var heartbeatTimer = new Timer(
            _ => WriteHeartbeat(attempt, "running"),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        try
        {
            WriteHeartbeat(attempt, "running");
            heartbeatTimer.Change(_heartbeatInterval, _heartbeatInterval);
            run = string.Equals(attempt.Kind, GateDispatchKind, StringComparison.Ordinal)
                ? _tryRunPreSlot?.Invoke(candidate, policy)
                : null;
            if (run is null)
            {
                run = RunWithAttemptTelemetryContext(
                    attempt,
                    candidate,
                    policy,
                    runAcceptance,
                    lease =>
                    {
                        stableSlotLease = lease;
                        AcceptanceAttemptArtifactCustody.Write(
                            lease.Environment.ArtifactsPath,
                            attempt.AttemptId,
                            attempt.MetadataPath,
                            Environment.ProcessId);
                    });
            }
        }
        catch (OperationCanceledException ex)
        {
            terminalWithoutResult = (ConductorParallelAcceptanceAttemptOutcome.Cancelled, ex.Message, false);
        }
        catch (Exception ex) when (IsTransientAttemptIo(ex))
        {
            terminalWithoutResult = (ConductorParallelAcceptanceAttemptOutcome.CorruptArtifacts, ex.Message, true);
        }
        catch (Exception ex)
        {
            run = ConductorParallelAcceptanceRunResult.Fault(candidate, ex);
            stderrDetail = ex.ToString();
        }
        finally
        {
            try
            {
                if (run is not null)
                {
                    CompleteWithRunResult(attempt, run, stderrDetail);
                }
                else if (terminalWithoutResult is { } terminal)
                {
                    CompleteWithoutResult(attempt, terminal.Outcome, terminal.Detail, terminal.Transient);
                }
            }
            finally
            {
                try
                {
                    if (stableSlotLease is not null)
                    {
                        AcceptanceAttemptArtifactCustody.Release(
                            stableSlotLease.Environment.ArtifactsPath,
                            attempt.AttemptId);
                    }
                }
                finally
                {
                    if (stableSlotLease is not null)
                    {
                        stableSlotLease.Dispose();
                        EmitAttemptLeaseReceipt(
                            "release",
                            attempt,
                            candidate,
                            Environment.ProcessId,
                            PermitName(stableSlotLease.Environment));
                    }
                }
            }
        }
    }

    private void CompleteWithRunResult(
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceRunResult run,
        string? stderrDetail)
    {
        try
        {
            WriteResult(attempt.ResultPath, ToArtifact(run));
            var outcome = OutcomeFor(run, attempt.Kind);
            TryWriteExit(attempt.ExitCodePath, outcome == ConductorParallelAcceptanceAttemptOutcome.Passed ? 0 : 1);
            TryPersistTerminal(attempt, current => current with
            {
                BranchHeadSha = run.Candidate.BranchHeadSha,
                MainHeadSha = run.Candidate.MainHeadSha,
                Outcome = outcome,
                CompletedAt = _utcNow(),
                LastHeartbeatAt = _utcNow(),
                Detail = AcceptanceRunDetail(run),
                TestResultPaths = ResultTestPaths(run)
            });
            if (!string.IsNullOrWhiteSpace(stderrDetail))
            {
                TryAppend(attempt.StderrPath, $"{stderrDetail}{Environment.NewLine}");
            }

            File.AppendAllText(attempt.StdoutPath, $"{attempt.Kind} attempt {attempt.AttemptId} completed outcome={outcome}{Environment.NewLine}");
            WriteHeartbeat(attempt, "exiting");
        }
        catch (Exception ex) when (IsTransientAttemptIo(ex))
        {
            CompleteWithoutResult(
                attempt,
                ConductorParallelAcceptanceAttemptOutcome.CorruptArtifacts,
                ex.Message,
                transient: true);
        }
        catch (Exception ex)
        {
            CompleteWithoutResult(
                attempt,
                FaultOutcomeFor(attempt.Kind),
                ex.Message);
            TryAppend(attempt.StderrPath, $"{ex}{Environment.NewLine}");
        }
    }

    private DotnetBuildEnvironmentLease AcquireAttemptStableSlotLease(
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceCandidate candidate)
    {
        var purpose = string.Equals(attempt.Kind, PreReviewEvidenceDispatchKind, StringComparison.Ordinal)
            ? "pre-review-evidence"
            : "parallel-acceptance";
        var environment = DotnetBuildEnvironmentManager.CreateAttempt(
            candidate.Goal.Id,
            $"{purpose}-{attempt.AttemptId}");
        var acquisition = DotnetBuildEnvironmentManager.TryAcquireLeaseExecutionLock(
            environment,
            DotnetBuildEnvironmentManager.DefaultSlotBusyPollTimeout);
        if (acquisition is DotnetBuildLeaseAcquisition.Acquired acquired)
        {
            var permitName = PermitName(acquired.Lease.Environment);
            EmitAttemptLeaseReceipt("acquire", attempt, candidate, Environment.ProcessId, permitName);
            EmitAttemptLeaseReceipt("handoff", attempt, candidate, Environment.ProcessId, permitName);
            return acquired.Lease;
        }

        if (acquisition is DotnetBuildLeaseAcquisition.SlotsBusy busy)
        {
            var holderPid = busy.BusySlots.FirstOrDefault(slot =>
                slot.SlotIndex == environment.BuildPermitIndex)?.OwnerProcessId;
            EmitAttemptLeaseReceipt("yield", attempt, candidate, holderPid, PermitName(environment));
            throw new DotnetBuildSlotsBusyException(busy);
        }

        if (acquisition is DotnetBuildLeaseAcquisition.BuildLockBlocked blocked)
        {
            throw new BuildLockBlockedException(blocked.Attribution);
        }

        throw new InvalidOperationException("Unknown dotnet build lease acquisition result.");
    }

    private ConductorParallelAcceptanceRunResult RunWithAttemptTelemetryContext(
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        ConductorParallelAcceptanceRunAcceptance runAcceptance,
        Action<DotnetBuildEnvironmentLease> leaseAcquired)
    {
        var previous = Environment.GetEnvironmentVariable(GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable);
        var previousAttemptId = Environment.GetEnvironmentVariable(
            AcceptanceAttemptArtifactCustody.AttemptIdVariable);
        var previousLivenessHint = Environment.GetEnvironmentVariable(
            AcceptanceAttemptArtifactCustody.LivenessCheckHintVariable);
        var prefix = Path.Combine(Path.GetDirectoryName(attempt.MetadataPath) ?? Environment.CurrentDirectory, attempt.AttemptId);
        Environment.SetEnvironmentVariable(GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable, prefix);
        Environment.SetEnvironmentVariable(
            AcceptanceAttemptArtifactCustody.AttemptIdVariable,
            attempt.AttemptId);
        Environment.SetEnvironmentVariable(
            AcceptanceAttemptArtifactCustody.LivenessCheckHintVariable,
            attempt.MetadataPath);
        try
        {
            var stableSlotLease = _acquireStableSlotLease(attempt, candidate);
            if (stableSlotLease is not null)
            {
                leaseAcquired(stableSlotLease);
            }
            return runAcceptance(candidate, policy, stableSlotLease, CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable(GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable, previous);
            Environment.SetEnvironmentVariable(
                AcceptanceAttemptArtifactCustody.AttemptIdVariable,
                previousAttemptId);
            Environment.SetEnvironmentVariable(
                AcceptanceAttemptArtifactCustody.LivenessCheckHintVariable,
                previousLivenessHint);
        }
    }

    private void EmitAttemptLeaseReceipt(
        string action,
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceCandidate candidate,
        int? holderPid,
        string? permitName = null)
    {
        var pid = holderPid?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
        var slot = permitName ?? $"acceptance-{candidate.SlotIndex}";
        var line = $"ACCEPTANCE_LEASE_{action.ToUpperInvariant()} goal={attempt.GoalPrefix} attempt={attempt.AttemptId} permit={slot} holderPid={pid}";
        Console.WriteLine(line);
        PersistLeaseReceipt(attempt, line);
    }

    private static string PermitName(DotnetBuildEnvironment environment) =>
        environment.BuildPermitIndex is { } permitIndex
            ? $"build-{permitIndex}"
            : Path.GetFileNameWithoutExtension(environment.ExecutionLockPath);

    private void PersistLeaseReceipt(ConductorParallelAcceptanceAttempt attempt, string line)
    {
        lock (MetadataWriteGate)
        {
            var current = TryReadAttemptFile(attempt.MetadataPath);
            if (current is null || !string.Equals(current.AttemptId, attempt.AttemptId, StringComparison.Ordinal))
            {
                return;
            }

            var receipts = (current.LeaseReceipts ?? []).ToList();
            if (receipts.Contains(line, StringComparer.Ordinal))
            {
                return;
            }

            receipts.Add(line);
            WriteAttemptFile(current with
            {
                LeaseReceipts = receipts,
                LastHeartbeatAt = _utcNow()
            });
        }
    }

    private void CompleteWithoutResult(
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceAttemptOutcome outcome,
        string detail,
        bool transient = false)
    {
        TryPersistTerminal(attempt, current => current with
        {
            Outcome = outcome,
            CompletedAt = _utcNow(),
            LastHeartbeatAt = _utcNow(),
            Detail = detail,
            TransientFailureCount = transient
                ? CountConsecutiveTransientFailures(current) + 1
                : current.TransientFailureCount
        });
        TryWriteExit(attempt.ExitCodePath, 1);
        TryAppend(attempt.StderrPath, $"{outcome}: {detail}{Environment.NewLine}");
        WriteHeartbeat(attempt, "exiting");
    }

    private ConductorParallelAcceptanceAttemptDecision? TryCompleteRunningAttempt(
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceCandidate candidate)
    {
        ConductorParallelAcceptanceAttemptDecision durablePassed;
        if (File.Exists(attempt.ResultPath))
        {
            try
            {
                var artifact = JsonSerializer.Deserialize<ConductorParallelAcceptanceRunArtifact>(
                    ReadAllTextSharedWithRetry(attempt.ResultPath),
                    JsonOptions);
                if (artifact is null)
                {
                    return MarkCorrupt(attempt, "result artifact was empty");
                }

                var run = FromArtifact(candidate, artifact);
                var completed = PersistResultCandidate(attempt, run);
                return ConductorParallelAcceptanceAttemptDecision.Completed(completed, run);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                if (ex is IOException or UnauthorizedAccessException)
                {
                    if (TryBuildDurablePassedCompletion(attempt, candidate, out durablePassed))
                    {
                        return durablePassed;
                    }

                    return _isProcessAlive(attempt.OwnerProcessId) ? null : MarkTransientArtifactReadFailure(attempt, ex.Message);
                }

                return MarkCorrupt(attempt, ex.Message);
            }
        }

        if (File.Exists(attempt.ExitCodePath))
        {
            var latest = TryReadAttemptFile(attempt.MetadataPath);
            if (latest is not null && TryBuildDurablePassedCompletion(latest, candidate, out durablePassed))
            {
                return durablePassed;
            }

            if (latest is not null && IsTerminalWithoutRunOutcome(latest.Outcome))
            {
                return ConductorParallelAcceptanceAttemptDecision.TerminalWithoutRun(latest);
            }

            return MarkCorrupt(attempt, "exit artifact exists without a result artifact");
        }

        if (!_isProcessAlive(attempt.OwnerProcessId))
        {
            var latest = TryReadAttemptFile(attempt.MetadataPath);
            if (latest is not null && TryBuildDurablePassedCompletion(latest, candidate, out durablePassed))
            {
                return durablePassed;
            }

            if (latest is not null && IsTerminalWithoutRunOutcome(latest.Outcome))
            {
                return ConductorParallelAcceptanceAttemptDecision.TerminalWithoutRun(latest);
            }

            var effectiveAttempt = latest ?? attempt;
            if (!IsHeartbeatStale(effectiveAttempt))
            {
                return null;
            }

            var dead = effectiveAttempt with
            {
                Outcome = ConductorParallelAcceptanceAttemptOutcome.ProcessDied,
                CompletedAt = _utcNow(),
                Detail = "owner process was not alive, heartbeat was stale, and no terminal result artifact existed",
                TransientFailureCount = CountConsecutiveTransientFailures(effectiveAttempt) + 1
            };
            Persist(dead);
            return ConductorParallelAcceptanceAttemptDecision.TerminalWithoutRun(dead);
        }

        return null;
    }

    private bool MarkStale(
        ConductorParallelAcceptanceAttempt attempt,
        ConductorEvidenceSupersessionCause supersessionCause,
        string detail = "candidate branch/main SHA moved before reconciliation")
    {
        lock (MetadataWriteGate)
        {
            return MarkStaleUnderLock(attempt, detail, supersessionCause);
        }
    }

    private bool MarkStaleUnderLock(
        ConductorParallelAcceptanceAttempt attempt,
        string detail,
        ConductorEvidenceSupersessionCause supersessionCause)
    {
        var current = TryReadAttemptFile(attempt.MetadataPath);
        if (current is null ||
            !string.Equals(current.AttemptId, attempt.AttemptId, StringComparison.Ordinal) ||
            IsReconciled(current))
        {
            return false;
        }

        WriteAttemptFile(current with
        {
            Outcome = ConductorParallelAcceptanceAttemptOutcome.StaleCandidate,
            CompletedAt = _utcNow(),
            ReconciledAt = _utcNow(),
            LastHeartbeatAt = _utcNow(),
            Detail = detail,
            SupersessionCause = supersessionCause
        });
        return true;
    }

    private void CompleteSupersession(
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceAttempt successor)
    {
        ConductorParallelAcceptanceAttempt superseded;
        lock (MetadataWriteGate)
        {
            var current = TryReadAttemptFile(attempt.MetadataPath) ?? attempt;
            superseded = current with
            {
                SupersededBy = successor.AttemptId,
                SupersessionCause = current.SupersessionCause ?? ConductorEvidenceSupersessionCause.CoordinatorReplacement,
                CompletedAt = current.CompletedAt ?? _utcNow(),
                LastHeartbeatAt = _utcNow()
            };
            WriteAttemptFile(superseded);
        }

        // The old terminal event must be durable before the successor start is visible.
        EmitEvidenceEnd(superseded);
    }

    private bool TryBuildDurablePassedCompletion(
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceCandidate candidate,
        out ConductorParallelAcceptanceAttemptDecision decision)
    {
        decision = null!;
        var latest = TryReadAttemptFile(attempt.MetadataPath) ?? attempt;
        if (latest.Outcome != ConductorParallelAcceptanceAttemptOutcome.Passed ||
            string.Equals(latest.Kind, PreReviewEvidenceDispatchKind, StringComparison.Ordinal))
        {
            return false;
        }

        var effectiveCandidate = ConductorParallelAcceptanceCandidate.Create(
            candidate.Goal,
            latest.SlotIndex,
            latest.ScopePaths ?? candidate.ScopePaths,
            latest.BranchHeadSha ?? candidate.BranchHeadSha,
            latest.MainHeadSha ?? candidate.MainHeadSha);
        var run = ConductorParallelAcceptanceRunResult.Accepted(
            effectiveCandidate,
            new AcceptanceVerificationSummary(
                true,
                [],
                BranchHeadSha: latest.BranchHeadSha,
                MainHeadSha: latest.MainHeadSha,
                TestResultPaths: latest.TestResultPaths));
        var completed = PersistResultCandidate(latest, run);
        decision = ConductorParallelAcceptanceAttemptDecision.Completed(completed, run);
        return true;
    }

    private ConductorParallelAcceptanceAttempt PersistResultCandidate(
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceRunResult run)
    {
        lock (MetadataWriteGate)
        {
            var current = TryReadAttemptFile(attempt.MetadataPath) ?? attempt;
            if (!string.Equals(current.AttemptId, attempt.AttemptId, StringComparison.Ordinal))
            {
                return current;
            }

            var updated = current with
            {
                BranchHeadSha = run.Candidate.BranchHeadSha,
                MainHeadSha = run.Candidate.MainHeadSha,
                Outcome = current.Outcome == ConductorParallelAcceptanceAttemptOutcome.Running
                    ? OutcomeFor(run, current.Kind)
                    : current.Outcome,
                CompletedAt = current.CompletedAt ?? _utcNow(),
                LastHeartbeatAt = _utcNow(),
                Detail = current.Detail ?? AcceptanceRunDetail(run),
                TestResultPaths = ResultTestPaths(run) ?? current.TestResultPaths
            };
            WriteAttemptFile(updated);
            return updated;
        }
    }

    private ConductorParallelAcceptanceAttemptDecision MarkCorrupt(
        ConductorParallelAcceptanceAttempt attempt,
        string detail)
    {
        var corrupt = attempt with
        {
            Outcome = ConductorParallelAcceptanceAttemptOutcome.CorruptArtifacts,
            CompletedAt = _utcNow(),
            Detail = detail
        };
        Persist(corrupt);
        return ConductorParallelAcceptanceAttemptDecision.TerminalWithoutRun(corrupt);
    }

    private ConductorParallelAcceptanceAttemptDecision MarkTransientArtifactReadFailure(
        ConductorParallelAcceptanceAttempt attempt,
        string detail)
    {
        var transient = attempt with
        {
            Outcome = ConductorParallelAcceptanceAttemptOutcome.CorruptArtifacts,
            CompletedAt = _utcNow(),
            Detail = detail,
            TransientFailureCount = CountConsecutiveTransientFailures(attempt) + 1
        };
        Persist(transient);
        return ConductorParallelAcceptanceAttemptDecision.TerminalWithoutRun(transient);
    }

    private ConductorParallelAcceptanceAttempt CreateAttempt(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        string dispatchKind,
        string? focusedEvidenceRequest)
    {
        var startedAt = _utcNow();
        var rawId = $"{candidate.GoalPrefix}-{candidate.SlotIndex}-{startedAt:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        var id = rawId[..Math.Min(64, rawId.Length)];
        var directory = Path.Combine(_rootDirectory, candidate.Goal.Id.Value);
        Directory.CreateDirectory(directory);
        PruneOldAttempts(directory, RetainedAttemptCountPerGoal - 1);
        var ordinal = AllocateOrdinal(directory);
        var prefix = Path.Combine(directory, id);
        return new ConductorParallelAcceptanceAttempt(
            id,
            candidate.Goal.Id.Value,
            candidate.GoalPrefix,
            candidate.SlotIndex,
            candidate.BranchHeadSha,
            candidate.MainHeadSha,
            startedAt,
            startedAt,
            Environment.ProcessId,
            ConductorParallelAcceptanceAttemptOutcome.Running,
            prefix + ".out.log",
            prefix + ".err.log",
            prefix + ".exit.txt",
            prefix + ".heartbeat.json",
            prefix + ".result.json",
            prefix + ".attempt.json",
            _executionDirectory,
            policy.Name,
            candidate.ScopePaths,
            Kind: dispatchKind,
            FocusedEvidenceRequest: focusedEvidenceRequest,
            Ordinal: ordinal,
            MonotonicStartedTimestamp: _timeProvider.GetTimestamp(),
            MonotonicTimestampFrequency: _timeProvider.TimestampFrequency,
            ConductEventLogPath: _conductEventLogWriter?.CurrentPath);
    }

    private static int AllocateOrdinal(string directory)
    {
        var sequencePath = Path.Combine(directory, "attempt-sequence.txt");
        lock (MetadataWriteGate)
        {
            var current = 0;
            if (File.Exists(sequencePath))
            {
                _ = int.TryParse(File.ReadAllText(sequencePath), NumberStyles.None, CultureInfo.InvariantCulture, out current);
            }
            else
            {
                current = Directory.EnumerateFiles(directory, "*.attempt.json").Count();
            }

            var next = checked(current + 1);
            File.WriteAllText(sequencePath, next.ToString(CultureInfo.InvariantCulture));
            return next;
        }
    }

    private ConductorParallelAcceptanceAttempt? TryReadLatest(string goalId)
    {
        var directory = Path.Combine(_rootDirectory, goalId);
        if (!Directory.Exists(directory))
        {
            return null;
        }

        ConductorParallelAcceptanceAttempt? latest = null;
        foreach (var path in Directory.EnumerateFiles(directory, "*.attempt.json"))
        {
            ConductorParallelAcceptanceAttempt? attempt;
            try
            {
                attempt = JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(
                    ReadAllTextSharedWithRetry(path),
                    JsonOptions);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return new ConductorParallelAcceptanceAttempt(
                    Path.GetFileNameWithoutExtension(path),
                    goalId,
                    goalId[..Math.Min(8, goalId.Length)],
                    0,
                    null,
                    null,
                    _utcNow(),
                    _utcNow(),
                    0,
                    ConductorParallelAcceptanceAttemptOutcome.CorruptArtifacts,
                    path + ".out.log",
                    path + ".err.log",
                    path + ".exit.txt",
                    path + ".heartbeat.json",
                    path + ".result.json",
                    path,
                    CompletedAt: _utcNow(),
                    Detail: ex.Message);
            }

            if (attempt is null)
            {
                continue;
            }

            latest = latest is null || attempt.StartedAt > latest.StartedAt ? attempt : latest;
        }

        return latest;
    }

    private void Persist(ConductorParallelAcceptanceAttempt attempt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(attempt.MetadataPath) ?? _rootDirectory);
        ConductorParallelAcceptanceAttempt persisted;
        lock (MetadataWriteGate)
        {
            persisted = attempt with { LastHeartbeatAt = _utcNow() };
            WriteAttemptFile(persisted);
        }

        EmitEvidenceEnd(persisted);
    }

    private bool TryPersistTerminal(
        ConductorParallelAcceptanceAttempt attempt,
        Func<ConductorParallelAcceptanceAttempt, ConductorParallelAcceptanceAttempt> transition)
    {
        ConductorParallelAcceptanceAttempt terminal;
        lock (MetadataWriteGate)
        {
            var current = TryReadAttemptFile(attempt.MetadataPath);
            if (current is null ||
                !string.Equals(current.AttemptId, attempt.AttemptId, StringComparison.Ordinal) ||
                current.Outcome != ConductorParallelAcceptanceAttemptOutcome.Running)
            {
                TryAppend(attempt.StderrPath, $"terminal outcome ignored because durable attempt is no longer running{Environment.NewLine}");
                return false;
            }

            terminal = transition(current);
            WriteAttemptFile(terminal);
        }

        EmitEvidenceEnd(terminal);
        return true;
    }

    private void EmitEvidenceStart(ConductorParallelAcceptanceAttempt attempt)
    {
        if (_conductEventLogWriter is null ||
            !string.Equals(attempt.Kind, PreReviewEvidenceDispatchKind, StringComparison.Ordinal))
        {
            return;
        }

        _conductEventLogWriter.AppendRequired(new ConductEvidenceLifecycleEvent(
            attempt.StartedAt,
            "EVIDENCE_START",
            attempt.GoalId,
            attempt.GoalId,
            attempt.AttemptId,
            attempt.Ordinal,
            $"evidence:{attempt.AttemptId}:start"));
    }

    private void EmitEvidenceEnd(ConductorParallelAcceptanceAttempt attempt)
    {
        if (_conductEventLogWriter is null ||
            !string.Equals(attempt.Kind, PreReviewEvidenceDispatchKind, StringComparison.Ordinal) ||
            attempt.Outcome is ConductorParallelAcceptanceAttemptOutcome.Running or ConductorParallelAcceptanceAttemptOutcome.Reconciled ||
            attempt.Outcome == ConductorParallelAcceptanceAttemptOutcome.StaleCandidate &&
            string.IsNullOrWhiteSpace(attempt.SupersededBy))
        {
            return;
        }

        var outcome = EvidenceOutcomeFor(attempt);
        _conductEventLogWriter.AppendRequired(new ConductEvidenceLifecycleEvent(
            attempt.CompletedAt ?? _utcNow(),
            "EVIDENCE_END",
            attempt.GoalId,
            attempt.GoalId,
            attempt.AttemptId,
            attempt.Ordinal,
            $"evidence:{attempt.AttemptId}:end",
            DurationSeconds(attempt),
            EvidenceToken(outcome),
            ExecutedTestCount(attempt.TestResultPaths),
            attempt.SupersededBy,
            attempt.SupersessionCause is { } cause ? EvidenceToken(cause) : null,
            attempt.Detail));
    }

    private object DurationSeconds(ConductorParallelAcceptanceAttempt attempt)
    {
        if (attempt.MonotonicStartedTimestamp is not { } started ||
            attempt.MonotonicTimestampFrequency != _timeProvider.TimestampFrequency)
        {
            return "unknown";
        }

        try
        {
            var elapsed = _timeProvider.GetElapsedTime(started, _timeProvider.GetTimestamp());
            return elapsed < TimeSpan.Zero
                ? "unknown"
                : Math.Round(elapsed.TotalSeconds, 3, MidpointRounding.AwayFromZero);
        }
        catch (ArgumentOutOfRangeException)
        {
            return "unknown";
        }
    }

    private static object ExecutedTestCount(IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0)
        {
            return "unknown";
        }

        long total = 0;
        try
        {
            foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(path))
                {
                    return "unknown";
                }

                var counters = XDocument.Load(path)
                    .Descendants()
                    .FirstOrDefault(element => element.Name.LocalName == "Counters");
                if (counters is null ||
                    !long.TryParse(counters.Attribute("passed")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var passed) ||
                    !long.TryParse(counters.Attribute("failed")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var failed))
                {
                    return "unknown";
                }

                total = checked(total + passed + failed);
            }

            return total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or OverflowException)
        {
            return "unknown";
        }
    }

    private static ConductorEvidenceAttemptOutcome EvidenceOutcomeFor(ConductorParallelAcceptanceAttempt attempt) =>
        attempt.Outcome switch
        {
            ConductorParallelAcceptanceAttemptOutcome.Passed => ConductorEvidenceAttemptOutcome.Passed,
            ConductorParallelAcceptanceAttemptOutcome.Failed => ConductorEvidenceAttemptOutcome.Failed,
            ConductorParallelAcceptanceAttemptOutcome.StaleCandidate when !string.IsNullOrWhiteSpace(attempt.SupersededBy) =>
                ConductorEvidenceAttemptOutcome.Superseded,
            ConductorParallelAcceptanceAttemptOutcome.Faulted => ConductorEvidenceAttemptOutcome.Faulted,
            ConductorParallelAcceptanceAttemptOutcome.Cancelled => ConductorEvidenceAttemptOutcome.Cancelled,
            ConductorParallelAcceptanceAttemptOutcome.LaunchFailed => ConductorEvidenceAttemptOutcome.LaunchFailed,
            ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot => ConductorEvidenceAttemptOutcome.BlockedBuildSlot,
            ConductorParallelAcceptanceAttemptOutcome.BlockedBuildLock => ConductorEvidenceAttemptOutcome.BlockedBuildLock,
            ConductorParallelAcceptanceAttemptOutcome.CorruptArtifacts => ConductorEvidenceAttemptOutcome.CorruptArtifacts,
            ConductorParallelAcceptanceAttemptOutcome.ProcessDied => ConductorEvidenceAttemptOutcome.Unknown,
            _ => ConductorEvidenceAttemptOutcome.Unknown
        };

    private static string EvidenceToken<T>(T value) where T : struct, Enum =>
        string.Concat(value.ToString().Select((character, index) =>
            char.IsUpper(character) && index > 0 ? $"_{char.ToLowerInvariant(character)}" : char.ToLowerInvariant(character).ToString()));

    private ConductorParallelAcceptanceAttempt TryPersistOwnerProcess(
        ConductorParallelAcceptanceAttempt attempt,
        int ownerProcessId)
    {
        lock (MetadataWriteGate)
        {
            var current = TryReadAttemptFile(attempt.MetadataPath);
            if (current is null ||
                !string.Equals(current.AttemptId, attempt.AttemptId, StringComparison.Ordinal))
            {
                var missing = attempt with
                {
                    OwnerProcessId = ownerProcessId,
                    LastHeartbeatAt = _utcNow()
                };
                WriteAttemptFile(missing);
                return missing;
            }

            if (current.Outcome != ConductorParallelAcceptanceAttemptOutcome.Running)
            {
                return current;
            }

            var updated = current with
            {
                OwnerProcessId = ownerProcessId,
                LastHeartbeatAt = _utcNow()
            };
            WriteAttemptFile(updated);
            return updated;
        }
    }

    private void WriteHeartbeat(ConductorParallelAcceptanceAttempt attempt, string state)
    {
        var now = _utcNow();
        var payload = new
        {
            kind = string.IsNullOrWhiteSpace(attempt.Kind) ? GateDispatchKind : attempt.Kind,
            pid = Environment.ProcessId,
            childPid = attempt.OwnerProcessId > 0 ? attempt.OwnerProcessId : (int?)null,
            ownedPids = attempt.OwnerProcessId > 0 ? new[] { attempt.OwnerProcessId } : Array.Empty<int>(),
            startedAt = attempt.StartedAt.ToString("O"),
            lastObservedAt = now.ToString("O"),
            lastProgressAt = now.ToString("O"),
            state,
            stdoutBytes = FileLength(attempt.StdoutPath),
            stderrBytes = FileLength(attempt.StderrPath),
            ownedCpuMs = 0L,
            exitFileExists = File.Exists(attempt.ExitCodePath)
        };

        try
        {
            var tmp = TemporarySiblingPath(attempt.HeartbeatPath);
            File.WriteAllText(tmp, JsonSerializer.Serialize(payload, JsonOptions));
            File.Move(tmp, attempt.HeartbeatPath, overwrite: true);
            _heartbeatWritten?.Invoke(attempt, state);
        }
        catch
        {
            // Heartbeat is evidence, not the gate result.
        }
    }

    private ConductorParallelAcceptanceOwnedProcessLaunchResult LaunchExternalOwnedProcess(
        ConductorParallelAcceptanceOwnedProcessLaunch launch)
    {
        var startInfo = BuildOwnedProcessStartInfo(launch.Attempt);
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("failed to start acceptance attempt process");
        var processId = process.Id;
        DetachOwnedProcessStreams(process, launch.Attempt);
        return new ConductorParallelAcceptanceOwnedProcessLaunchResult(processId);
    }

    internal static ProcessStartInfo BuildOwnedProcessStartInfo(
        ConductorParallelAcceptanceAttempt attempt,
        string? executable = null,
        IReadOnlyList<string>? commandLineArgs = null)
    {
        if (string.IsNullOrWhiteSpace(attempt.ExecutionDirectory))
        {
            throw new InvalidOperationException("acceptance attempt execution directory was not recorded");
        }

        executable ??= Environment.ProcessPath ?? "dotnet";
        commandLineArgs ??= Environment.GetCommandLineArgs();
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = attempt.ExecutionDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
            commandLineArgs.Count > 0)
        {
            startInfo.ArgumentList.Add(commandLineArgs[0]);
        }

        startInfo.ArgumentList.Add(OwnedProcessSubcommandName);
        startInfo.ArgumentList.Add(attempt.MetadataPath);
        // The owned lane process takes the stable-slot lease and produces the landing verdict itself. Scrubbing
        // only its test grandchildren is too late for lease-cleanup hatches and acceptance-scope overrides.
        GoalAcceptanceVerifier.ConfigureHermeticVerificationEnvironment(
            startInfo.Environment,
            attempt.ExecutionDirectory);
        return startInfo;
    }

    private static void DetachOwnedProcessStreams(Process process, ConductorParallelAcceptanceAttempt attempt)
    {
        var processId = process.Id;
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                TryAppend(attempt.StdoutPath, e.Data + Environment.NewLine);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                TryAppend(attempt.StderrPath, e.Data + Environment.NewLine);
            }
        };
        process.Exited += (_, _) =>
        {
            if (OwnedProcessDrains.TryRemove(processId, out var completed))
            {
                completed.Dispose();
            }
        };
        process.EnableRaisingEvents = true;
        OwnedProcessDrains[processId] = process;

        try { process.StandardInput.Close(); } catch { }
        try { process.BeginOutputReadLine(); } catch { }
        try { process.BeginErrorReadLine(); } catch { }
        try
        {
            if (process.HasExited && OwnedProcessDrains.TryRemove(processId, out var completed))
            {
                completed.Dispose();
            }
        }
        catch
        {
        }
    }

    private static void RedirectConsole(ConductorParallelAcceptanceAttempt attempt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(attempt.StdoutPath) ?? ".");
        var stdout = new StreamWriter(new FileStream(attempt.StdoutPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        var stderr = new StreamWriter(new FileStream(attempt.StderrPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        Console.SetOut(stdout);
        Console.SetError(stderr);
    }

    private static ConductorParallelAcceptanceRunArtifact ToArtifact(ConductorParallelAcceptanceRunResult run)
    {
        if (run.Exception is not null)
        {
            return new ConductorParallelAcceptanceRunArtifact(
                "fault",
                run.Exception switch
                {
                    DotnetBuildSlotsBusyException => "blocked-build-slot",
                    BuildLockBlockedException => "blocked-build-lock",
                    OperationCanceledException => "cancelled",
                    _ => "exception"
                },
                run.Exception.Message,
                null,
                null,
                null,
                run.Candidate.BranchHeadSha,
                run.Candidate.MainHeadSha,
                null,
                null);
        }

        if (run.EarlyResult is { Outcome: var outcome })
        {
            return outcome switch
            {
                ConductorAdvanceOutcome.Held held => EarlyArtifact("early-held", held.State, held.Reason, run),
                ConductorAdvanceOutcome.Escalated escalated => EarlyArtifact("early-escalated", escalated.State, escalated.Reason, run),
                ConductorAdvanceOutcome.Done done => EarlyArtifact("early-done", done.State, run.EarlyOutcome?.Detail, run),
                ConductorAdvanceOutcome.Executed executed => EarlyArtifact("early-executed", executed.FromState, executed.Description, run),
                _ => new("fault", "exception", "unknown early acceptance result", null, null, null, run.Candidate.BranchHeadSha, run.Candidate.MainHeadSha, null, null)
            };
        }

        if (run.FocusedEvidence is not null)
        {
            return new ConductorParallelAcceptanceRunArtifact(
                "focused-evidence",
                null,
                null,
                null,
                null,
                null,
                run.Candidate.BranchHeadSha,
                run.Candidate.MainHeadSha,
                null,
                null,
                FocusedEvidence: run.FocusedEvidence);
        }

        return new ConductorParallelAcceptanceRunArtifact(
            "accepted",
            null,
            null,
            null,
            null,
            run.Acceptance,
            run.Candidate.BranchHeadSha,
            run.Candidate.MainHeadSha,
            null,
            null);
    }

    private static ConductorParallelAcceptanceRunArtifact EarlyArtifact(
        string kind,
        GoalLifecycleState state,
        string? message,
        ConductorParallelAcceptanceRunResult run) =>
        new(
            kind,
            null,
            null,
            state.ToString(),
            message,
            null,
            run.Candidate.BranchHeadSha,
            run.Candidate.MainHeadSha,
            run.EarlyOutcome?.Kind,
            run.EarlyOutcome?.Detail);

    private static ConductorParallelAcceptanceRunResult FromArtifact(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorParallelAcceptanceRunArtifact artifact)
    {
        var effectiveCandidate = ConductorParallelAcceptanceCandidate.Create(
            candidate.Goal,
            candidate.SlotIndex,
            candidate.ScopePaths,
            artifact.BranchHeadSha ?? candidate.BranchHeadSha,
            artifact.MainHeadSha ?? candidate.MainHeadSha);

        return artifact.Kind switch
        {
            "accepted" when artifact.Acceptance is not null =>
                ConductorParallelAcceptanceRunResult.Accepted(effectiveCandidate, artifact.Acceptance),
            "focused-evidence" when artifact.FocusedEvidence is not null =>
                ConductorParallelAcceptanceRunResult.Focused(effectiveCandidate, artifact.FocusedEvidence),
            "early-held" => ConductorParallelAcceptanceRunResult.Early(
                effectiveCandidate,
                new ConductorAdvanceResult(
                    effectiveCandidate.Goal.Id.Value,
                    effectiveCandidate.GoalPrefix,
                    string.Empty,
                    new ConductorAdvanceOutcome.Held(ParseState(artifact.State), artifact.Message ?? "held")),
                RehydrateEarlyOutcome(artifact)),
            "early-escalated" => ConductorParallelAcceptanceRunResult.Early(
                effectiveCandidate,
                new ConductorAdvanceResult(
                    effectiveCandidate.Goal.Id.Value,
                    effectiveCandidate.GoalPrefix,
                    string.Empty,
                    new ConductorAdvanceOutcome.Escalated(ParseState(artifact.State), artifact.Message ?? "escalated")),
                RehydrateEarlyOutcome(artifact)),
            "early-done" => ConductorParallelAcceptanceRunResult.Early(
                effectiveCandidate,
                new ConductorAdvanceResult(
                    effectiveCandidate.Goal.Id.Value,
                    effectiveCandidate.GoalPrefix,
                    string.Empty,
                    new ConductorAdvanceOutcome.Done(ParseState(artifact.State))),
                RehydrateEarlyOutcome(artifact)),
            "early-executed" => ConductorParallelAcceptanceRunResult.Early(
                effectiveCandidate,
                new ConductorAdvanceResult(
                    effectiveCandidate.Goal.Id.Value,
                    effectiveCandidate.GoalPrefix,
                    string.Empty,
                    new ConductorAdvanceOutcome.Executed(ParseState(artifact.State), artifact.Message ?? "executed")),
                RehydrateEarlyOutcome(artifact)),
            "fault" => ConductorParallelAcceptanceRunResult.Fault(effectiveCandidate, RehydrateFault(artifact)),
            _ => throw new InvalidOperationException("unrecognized acceptance attempt result artifact")
        };
    }

    private static ConductorParallelAcceptanceEarlyOutcome? RehydrateEarlyOutcome(
        ConductorParallelAcceptanceRunArtifact artifact) =>
        string.IsNullOrWhiteSpace(artifact.EarlyOutcomeKind)
            ? null
            : new ConductorParallelAcceptanceEarlyOutcome(
                artifact.EarlyOutcomeKind,
                ParseState(artifact.State),
                artifact.EarlyOutcomeDetail ?? artifact.Message ?? artifact.EarlyOutcomeKind);

    private static Exception RehydrateFault(ConductorParallelAcceptanceRunArtifact artifact) =>
        artifact.FaultKind switch
        {
            "blocked-build-slot" => new DotnetBuildSlotsBusyException(
                new DotnetBuildLeaseAcquisition.SlotsBusy("background-acceptance", [])),
            "blocked-build-lock" => new BuildLockBlockedException(
                new BuildLockAttribution("unknown", [], "background-acceptance", "acceptance", "background-acceptance")),
            "cancelled" => new OperationCanceledException(artifact.FaultMessage),
            _ => new InvalidOperationException(artifact.FaultMessage ?? "background acceptance failed")
        };

    private static GoalLifecycleState ParseState(string? value) =>
        Enum.TryParse<GoalLifecycleState>(value, out var state) ? state : GoalLifecycleState.Verified;

    private static ConductorParallelAcceptanceAttemptOutcome OutcomeFor(
        ConductorParallelAcceptanceRunResult run,
        string dispatchKind)
    {
        if (run.Exception is DotnetBuildSlotsBusyException)
        {
            return ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot;
        }

        if (run.Exception is BuildLockBlockedException)
        {
            return ConductorParallelAcceptanceAttemptOutcome.BlockedBuildLock;
        }

        if (run.Exception is OperationCanceledException)
        {
            return ConductorParallelAcceptanceAttemptOutcome.Cancelled;
        }

        if (run.Exception is not null)
        {
            return FaultOutcomeFor(dispatchKind);
        }

        if (run.EarlyResult is not null)
        {
            return run.EarlyResult.WasEscalated
                ? ConductorParallelAcceptanceAttemptOutcome.Failed
                : ConductorParallelAcceptanceAttemptOutcome.Passed;
        }

        if (run.FocusedEvidence is not null)
        {
            return run.FocusedEvidence.Passed
                ? ConductorParallelAcceptanceAttemptOutcome.Passed
                : ConductorParallelAcceptanceAttemptOutcome.Failed;
        }

        return run.Acceptance?.Passed == true
            ? ConductorParallelAcceptanceAttemptOutcome.Passed
            : ConductorParallelAcceptanceAttemptOutcome.Failed;
    }

    private static ConductorParallelAcceptanceAttemptOutcome FaultOutcomeFor(string dispatchKind) =>
        string.Equals(dispatchKind, PreReviewEvidenceDispatchKind, StringComparison.Ordinal)
            ? ConductorParallelAcceptanceAttemptOutcome.Faulted
            : ConductorParallelAcceptanceAttemptOutcome.Failed;

    private static bool IsTerminalWithoutRunOutcome(ConductorParallelAcceptanceAttemptOutcome outcome) =>
        outcome is ConductorParallelAcceptanceAttemptOutcome.StaleCandidate
            or ConductorParallelAcceptanceAttemptOutcome.ProcessDied
            or ConductorParallelAcceptanceAttemptOutcome.CorruptArtifacts
            or ConductorParallelAcceptanceAttemptOutcome.Cancelled
            or ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot
            or ConductorParallelAcceptanceAttemptOutcome.BlockedBuildLock
            or ConductorParallelAcceptanceAttemptOutcome.LaunchFailed;

    private static bool IsReconciled(ConductorParallelAcceptanceAttempt attempt) =>
        attempt.ReconciledAt.HasValue ||
        attempt.Outcome == ConductorParallelAcceptanceAttemptOutcome.Reconciled;

    private static string AcceptanceRunDetail(ConductorParallelAcceptanceRunResult run)
    {
        if (run.Exception is not null)
        {
            return run.Exception.Message;
        }

        if (run.EarlyResult is not null)
        {
            return run.EarlyOutcome?.Detail ?? run.EarlyResult.Outcome.ToString() ?? "early result";
        }

        if (run.FocusedEvidence is not null)
        {
            return run.FocusedEvidence.Summary;
        }

        return run.Acceptance?.Passed == true ? "acceptance passed" : "acceptance failed";
    }

    private static IReadOnlyList<string>? ResultTestPaths(ConductorParallelAcceptanceRunResult run) =>
        run.Acceptance?.TestResultPaths ?? run.FocusedEvidence?.Checks
            .SelectMany(check => check.TestResultPaths ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static void WriteResult(string path, ConductorParallelAcceptanceRunArtifact artifact)
    {
        var tmp = TemporarySiblingPath(path);
        WriteAllTextDurable(tmp, JsonSerializer.Serialize(artifact, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    private static string TemporarySiblingPath(string path) =>
        $"{path}.{Guid.NewGuid():N}.tmp";

    private static void PruneOldAttempts(string directory, int retainCount)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        var staleAttempts = Directory.EnumerateFiles(directory, "*.attempt.json")
            .Select(path => new
            {
                Path = path,
                Attempt = TryReadAttemptFile(path),
                Timestamp = File.GetLastWriteTimeUtc(path)
            })
            .OrderByDescending(item => item.Attempt?.StartedAt.UtcDateTime ?? item.Timestamp)
            .Skip(Math.Max(0, retainCount))
            .ToArray();

        foreach (var item in staleAttempts)
        {
            var prefix = item.Path[..^".attempt.json".Length];
            foreach (var path in Directory.EnumerateFiles(directory, Path.GetFileName(prefix) + ".*"))
            {
                TryDeleteFile(path);
            }

            var receiptDirectory = prefix + ".receipts";
            if (Directory.Exists(receiptDirectory))
            {
                try
                {
                    Directory.Delete(receiptDirectory, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine(
                        $"ATTEMPT_RECEIPT_PRUNE_FAILED path=\"{receiptDirectory}\" error=\"{ex.Message}\"");
                }
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private static ConductorParallelAcceptanceAttempt? TryReadAttemptFile(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(
                    ReadAllTextSharedWithRetry(path),
                    JsonOptions)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteAttemptFile(ConductorParallelAcceptanceAttempt attempt)
    {
        var payload = JsonSerializer.Serialize(attempt, JsonOptions);
        for (var retry = 0; ; retry++)
        {
            var tmp = TemporarySiblingPath(attempt.MetadataPath);
            WriteAllTextDurable(tmp, payload);
            try
            {
                File.Move(tmp, attempt.MetadataPath, overwrite: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && retry < 10)
            {
                try { File.Delete(tmp); } catch { }
                Thread.Sleep(TimeSpan.FromMilliseconds(25 * (retry + 1)));
            }
        }
    }

    private static bool IsProcessAlive(int processId)
    {
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static long FileLength(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return 0L;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return stream.Length;
        }
        catch
        {
            return 0L;
        }
    }

    private static void TryWriteExit(string path, int exitCode)
    {
        try { File.WriteAllText(path, exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        catch { }
    }

    private static void TryAppend(string path, string text)
    {
        try { File.AppendAllText(path, text); }
        catch { }
    }

    private int CountConsecutiveTransientFailures(ConductorParallelAcceptanceAttempt attempt)
    {
        var directory = Path.GetDirectoryName(attempt.MetadataPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return 0;
        }

        foreach (var prior in Directory.EnumerateFiles(directory, "*.attempt.json")
            .Select(TryReadAttemptFile)
            .OfType<ConductorParallelAcceptanceAttempt>()
            .Where(candidate =>
                string.Equals(candidate.CandidateKey, attempt.CandidateKey, StringComparison.Ordinal) &&
                !string.Equals(candidate.AttemptId, attempt.AttemptId, StringComparison.Ordinal))
            .OrderByDescending(candidate => candidate.StartedAt))
        {
            return IsTransientTerminalFailure(prior)
                ? Math.Max(1, prior.TransientFailureCount)
                : 0;
        }

        return 0;
    }

    internal static bool IsTransientTerminalFailure(ConductorParallelAcceptanceAttempt attempt) =>
        attempt.TransientFailureCount > 0 &&
        (attempt.Outcome == ConductorParallelAcceptanceAttemptOutcome.LaunchFailed ||
            attempt.Outcome == ConductorParallelAcceptanceAttemptOutcome.CorruptArtifacts ||
            attempt.Outcome == ConductorParallelAcceptanceAttemptOutcome.ProcessDied);

    private bool IsHeartbeatStale(ConductorParallelAcceptanceAttempt attempt)
    {
        var observedAt = ReadHeartbeatObservedAt(attempt.HeartbeatPath) ?? attempt.LastHeartbeatAt;
        return _utcNow() - observedAt >= _recentHeartbeatGrace;
    }

    private static DateTimeOffset? ReadHeartbeatObservedAt(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var document = JsonDocument.Parse(ReadAllTextSharedWithRetry(path));
            return document.RootElement.TryGetProperty("lastObservedAt", out var observedAt) &&
                observedAt.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(observedAt.GetString(), out var parsed)
                    ? parsed
                    : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteAllTextDurable(string path, string payload)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        using var writer = new StreamWriter(stream);
        writer.Write(payload);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }

    private static bool IsTransientAttemptIo(Exception ex) =>
        ex is not DotnetBuildSlotsBusyException and not BuildLockBlockedException &&
        (ex is IOException or UnauthorizedAccessException ||
            ex.InnerException is not null && IsTransientAttemptIo(ex.InnerException));

    private static string ReadAllTextSharedWithRetry(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException ex) when (IsSharingViolation(ex) && attempt < 5)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(25 * (attempt + 1)));
            }
        }
    }

    private static bool IsSharingViolation(IOException ex)
    {
        var code = ex.HResult & 0xFFFF;
        return code is 32 or 33;
    }
}

internal sealed record ConductorParallelAcceptanceRunArtifact(
    string Kind,
    string? FaultKind,
    string? FaultMessage,
    string? State,
    string? Message,
    AcceptanceVerificationSummary? Acceptance,
    string? BranchHeadSha,
    string? MainHeadSha,
    string? EarlyOutcomeKind,
    string? EarlyOutcomeDetail,
    string DispatchKind = ConductorParallelAcceptanceAttemptCoordinator.GateDispatchKind,
    FocusedEvidenceRunResult? FocusedEvidence = null);
