using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
    InfrastructureDeferred,
    GateEngineFault,
    LaunchFailed,
    Faulted,
    Reconciled,
    StructuralCoveragePermitUnavailable
}

internal enum WorkerRegistrationFaultDisposition
{
    None,
    BoundedRetry,
    Terminal
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
    InfrastructureDeferred,
    GateEngineFault,
    CorruptArtifacts,
    Unknown,
    StructuralCoveragePermitUnavailable
}

internal enum ConductorEvidenceSupersessionCause
{
    RetryInvalidated,
    CandidateChanged,
    FindingRoundChanged,
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

internal enum AcceptanceBuildPermitWaitReason
{
    AllPermitsBusy,
    DesignatedPermitBusyWhileFree
}

internal enum AcceptanceStableSlotExhaustionPolicy
{
    Fail,
    DegradeToSerial
}

internal sealed class AcceptanceArtifactWriterLeaseBusyException(
    string goalDirectory,
    string? observedAttemptId = null)
    : Exception(
        observedAttemptId is null
            ? $"Acceptance artifact writer lease is held before a canonical attempt is observable in '{goalDirectory}'."
            : $"Acceptance artifact writer lease is held while observed attempt '{observedAttemptId}' is not live in '{goalDirectory}'.")
{
    public string GoalDirectory { get; } = goalDirectory;

    public string? ObservedAttemptId { get; } = observedAttemptId;
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
    ConductorEvidenceSupersessionCause? SupersessionCause = null,
    AcceptanceBuildPermitWaitReason? BuildPermitWaitReason = null,
    string? FocusedEvidenceBatchId = null,
    IReadOnlyList<string>? FocusedEvidenceMemberRequests = null,
    string? FocusedEvidenceRequestDisposition = null,
    string? FindingRoundFingerprint = null,
    IReadOnlyList<FindingEvidenceRequestDisposition>? FocusedEvidenceRequestDispositions = null,
    string? FocusedEvidenceReceiptId = null,
    string? PolicyJson = null,
    AcceptanceStableSlotExhaustionPolicy? StableSlotExhaustionPolicy = null,
    string ExecutionProtocol = "out-of-process",
    // The conductor generation that started this attempt, and the one that later adopted it across a
    // renewal. Null means an attempt written before generation identity was recorded: unknown, not mine.
    int? ConductorGenerationId = null,
    int? AdoptedByGenerationId = null,
    bool FocusedEvidenceRunsBaselineArm = false,
    FocusedEvidenceRunResult? CandidateEvidenceBeforeBaseline = null)
{
    public string CandidateKey => $"{GoalId}:{BranchHeadSha ?? "unknown-branch"}:{MainHeadSha ?? "unknown-main"}";
}

internal sealed record ConductorFocusedEvidenceRequestContext(
    string FindingRoundFingerprint,
    string BatchId,
    IReadOnlyList<FindingEvidenceRequestDisposition> RequestDispositions,
    bool RunBaselineArm = false,
    FocusedEvidenceRunResult? CandidateEvidenceBeforeBaseline = null);

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
    Action<int> ExecuteInCurrentProcess,
    DotnetBuildStorageRoot? BuildStorageRoot);

internal sealed record ConductorParallelAcceptanceOwnedProcessLaunchResult(int ProcessId);

internal delegate ConductorParallelAcceptanceRunResult ConductorParallelAcceptanceRunAcceptance(
    ConductorParallelAcceptanceCandidate candidate,
    ConductorAutonomyPolicy policy,
    DotnetBuildEnvironmentLease? stableSlotLease,
    CancellationToken cancellationToken,
    AcceptanceRunExecutionOptions executionOptions);

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
    internal const int RetainedAttemptCountPerGoal = 20;
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
    private readonly Action<ConductorParallelAcceptanceAttempt, string>? _cleanupObservedForTests;
    private readonly Action<ConductorParallelAcceptanceAttempt>? _resultPublishedForTests;
    private readonly Action? _attemptWriterLeaseAcquiringForTests;
    private readonly Func<ConductorParallelAcceptanceAttempt, ConductorParallelAcceptanceCandidate, DotnetBuildEnvironmentLease?> _acquireStableSlotLease;
    private readonly IReadOnlyDictionary<string, TextWriter>? _attemptLogWriters;
    private readonly TimeSpan _buildPermitBusyTimeout;
    private readonly Action<TimeSpan>? _buildPermitSleep;
    private readonly ConductEventLogWriter? _conductEventLogWriter;
    private readonly DotnetBuildStorageRoot? _buildStorageRoot;
    private readonly int _conductorGenerationId;

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
        Action<ConductorParallelAcceptanceAttempt, string>? cleanupObservedForTests = null,
        Func<ConductorParallelAcceptanceAttempt, ConductorParallelAcceptanceCandidate, DotnetBuildEnvironmentLease?>? acquireStableSlotLease = null,
        ConductEventLogWriter? conductEventLogWriter = null,
        TimeProvider? timeProvider = null,
        TimeSpan? buildPermitBusyTimeout = null,
        Action<TimeSpan>? buildPermitSleep = null,
        Action? attemptWriterLeaseAcquiringForTests = null,
        IReadOnlyDictionary<string, TextWriter>? attemptLogWriters = null,
        Action<ConductorParallelAcceptanceAttempt>? resultPublishedForTests = null,
        DotnetBuildStorageRoot? buildStorageRoot = null,
        // Each conductor renewal is a fresh child process, so the loop pid is a sound generation
        // identity and the driver needs no threading change to supply one.
        int? conductorGenerationId = null)
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
        _cleanupObservedForTests = cleanupObservedForTests;
        _resultPublishedForTests = resultPublishedForTests;
        _attemptWriterLeaseAcquiringForTests = attemptWriterLeaseAcquiringForTests;
        _acquireStableSlotLease = acquireStableSlotLease ?? AcquireAttemptStableSlotLease;
        _attemptLogWriters = attemptLogWriters;
        _conductEventLogWriter = conductEventLogWriter;
        // Only an explicit operation setting crosses the hermetic child boundary.
        // An absent setting retains the existing per-process default resolution.
        _buildStorageRoot = buildStorageRoot;
        _buildPermitBusyTimeout = buildPermitBusyTimeout ?? DotnetBuildEnvironmentManager.DefaultSlotBusyPollTimeout;
        _buildPermitSleep = buildPermitSleep;
        _conductorGenerationId = conductorGenerationId ?? Environment.ProcessId;
    }

    internal ConductorParallelAcceptanceAttemptDecision Evaluate(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        ConductorParallelAcceptanceRunAcceptance runAcceptance,
        AcceptanceStableSlotExhaustionPolicy stableSlotExhaustionPolicy = AcceptanceStableSlotExhaustionPolicy.Fail)
        => EvaluateCore(
            candidate,
            policy,
            runAcceptance,
            GateDispatchKind,
            focusedEvidenceRequest: null,
            requestContext: null,
            stableSlotExhaustionPolicy);

    internal IReadOnlyList<ConductorParallelAcceptanceAttempt> GetUnreconciledAttempts(
        IEnumerable<string> goalIds)
    {
        ArgumentNullException.ThrowIfNull(goalIds);

        var attempts = new List<ConductorParallelAcceptanceAttempt>();
        foreach (var goalId in goalIds.Distinct(StringComparer.Ordinal))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(goalId);
            var directory = Path.Combine(_rootDirectory, goalId);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(directory, "*.attempt.json"))
            {
                var attempt = ReadCanonicalAttempt(path, goalId);
                if (!IsReconciled(attempt) || IsCapacityReservingAttempt(attempt))
                {
                    attempts.Add(attempt);
                }
            }
        }

        return attempts
            .OrderBy(attempt => attempt.GoalId, StringComparer.Ordinal)
            .ThenBy(attempt => attempt.StartedAt)
            .ThenBy(attempt => attempt.AttemptId, StringComparer.Ordinal)
            .ToArray();
    }

    internal ConductorParallelAcceptanceAttemptDecision ObserveExistingAttempt(
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(candidate);

        var current = ReadCanonicalAttempt(attempt.MetadataPath, attempt.GoalId);
        if (!string.Equals(current.AttemptId, attempt.AttemptId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Acceptance attempt identity changed at canonical metadata path '{attempt.MetadataPath}'.");
        }

        if (IsCapacityReservingInvalidatedAttempt(current))
        {
            return ConductorParallelAcceptanceAttemptDecision.Running(current);
        }

        if (IsReconciled(current))
        {
            if (IsTerminalWithoutRunOutcome(current.Outcome))
            {
                return ConductorParallelAcceptanceAttemptDecision.TerminalWithoutRun(current);
            }

            throw new InvalidDataException(
                $"Acceptance attempt '{current.AttemptId}' was selected for observation after reconciliation.");
        }

        if (IsTerminalWithoutRunOutcome(current.Outcome))
        {
            return ConductorParallelAcceptanceAttemptDecision.TerminalWithoutRun(current);
        }

        var decision = TryCompleteRunningAttempt(current, candidate) ??
            ConductorParallelAcceptanceAttemptDecision.Running(current);
        return decision with
        {
            Attempt = decision.Attempt with { MetadataPath = current.MetadataPath }
        };
    }

    internal DotnetBuildEnvironmentLease AcquireCohortStableSlotLease(
        string cohortId,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cohortId);
        var lease = DotnetBuildEnvironmentManager.AcquireFirstAvailableStableSlotExecutionLock(
            timeout ?? DotnetBuildEnvironmentManager.DefaultSlotBusyPollTimeout,
            cancellationToken: cancellationToken,
            slotCount: DotnetBuildEnvironmentManager.StableSlotCount,
            storageRoot: _buildStorageRoot);
        Console.WriteLine(
            $"ACCEPTANCE_LEASE_ACQUIRE cohort={cohortId} permit=acceptance-{lease.Environment.BuildPermitIndex?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} holderPid={Environment.ProcessId}");
        return lease;
    }

    internal ConductorParallelAcceptanceAttemptDecision EvaluateFocusedEvidence(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        string request,
        Func<Goal, string, DotnetBuildEnvironmentLease?, CancellationToken, FocusedEvidenceRunResult> runFocusedEvidence,
        ConductorFocusedEvidenceRequestContext? requestContext = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request);
        ArgumentNullException.ThrowIfNull(runFocusedEvidence);
        return EvaluateCore(
            candidate,
            policy,
            (attemptCandidate, _, lease, cancellationToken, _) => ConductorParallelAcceptanceRunResult.Focused(
                attemptCandidate,
                runFocusedEvidence(attemptCandidate.Goal, request, lease, cancellationToken)),
            PreReviewEvidenceDispatchKind,
            request,
            requestContext,
            AcceptanceStableSlotExhaustionPolicy.Fail);
    }

    private ConductorParallelAcceptanceAttemptDecision EvaluateCore(
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        ConductorParallelAcceptanceRunAcceptance runAcceptance,
        string dispatchKind,
        string? focusedEvidenceRequest,
        ConductorFocusedEvidenceRequestContext? requestContext,
        AcceptanceStableSlotExhaustionPolicy stableSlotExhaustionPolicy)
    {
        var current = TryReadLatest(candidate.Goal.Id.Value);
        if (current is not null && (IsLiveInvalidatedAttempt(current) || IsLiveAttempt(current)))
        {
            return ConductorParallelAcceptanceAttemptDecision.Running(current);
        }

        // Before any path that can reach Launch: an attempt left running by a dead generation whose gate
        // child is still alive is adopted, not replaced. Its verdict is applied from artifacts by the
        // existing completion path once the child publishes them, so the retry budget is charged once.
        if (current is not null &&
            MatchesCandidate(current, candidate, dispatchKind, focusedEvidenceRequest, requestContext) &&
            TryAdoptOrphanAttempt(current) is { } adopted)
        {
            return ConductorParallelAcceptanceAttemptDecision.Running(adopted);
        }

        _attemptWriterLeaseAcquiringForTests?.Invoke();
        var goalDirectory = Path.Combine(_rootDirectory, candidate.Goal.Id.Value);
        using var artifactLease = StorageRetentionMaintenance.TryAcquireAttemptWriterLease(goalDirectory);
        if (artifactLease is null)
        {
            current = TryReadLatest(candidate.Goal.Id.Value);
            if (current is not null && (IsLiveInvalidatedAttempt(current) || IsLiveAttempt(current)))
            {
                return ConductorParallelAcceptanceAttemptDecision.Running(current);
            }

            throw new AcceptanceArtifactWriterLeaseBusyException(goalDirectory, current?.AttemptId);
        }

        current = TryReadLatest(candidate.Goal.Id.Value);
        if (current is not null && IsLiveInvalidatedAttempt(current))
        {
            return ConductorParallelAcceptanceAttemptDecision.Running(current);
        }

        if (current is not null && IsReconciled(current))
        {
            if (current.Outcome == ConductorParallelAcceptanceAttemptOutcome.StaleCandidate &&
                current.SupersessionCause is not null)
            {
                var successor = CreateAttempt(
                    candidate,
                    policy,
                    dispatchKind,
                    focusedEvidenceRequest,
                    requestContext,
                    stableSlotExhaustionPolicy);
                CompleteSupersession(current, successor);
                return Launch(
                    candidate,
                    policy,
                    runAcceptance,
                    dispatchKind,
                    focusedEvidenceRequest,
                    requestContext,
                    stableSlotExhaustionPolicy,
                    successor);
            }

            current = null;
        }

        if (current is not null && IsTerminalWithoutRunOutcome(current.Outcome))
        {
            if (!MatchesCandidate(current, candidate, dispatchKind, focusedEvidenceRequest, requestContext))
            {
                return ReplaceStaleAttempt(
                    current,
                    candidate,
                    policy,
                    runAcceptance,
                    dispatchKind,
                    focusedEvidenceRequest,
                    requestContext,
                    stableSlotExhaustionPolicy);
            }

            return ConductorParallelAcceptanceAttemptDecision.TerminalWithoutRun(current);
        }

        if (current is not null && !IsReconciled(current))
        {
            var terminal = TryCompleteRunningAttempt(current, candidate);
            if (terminal is { Run: not null })
            {
                if (!MatchesCandidate(terminal.Attempt, candidate, dispatchKind, focusedEvidenceRequest, requestContext))
                {
                    return ReplaceStaleAttempt(
                        terminal.Attempt,
                        candidate,
                        policy,
                        runAcceptance,
                        dispatchKind,
                        focusedEvidenceRequest,
                        requestContext,
                        stableSlotExhaustionPolicy);
                }

                return terminal;
            }

            if (terminal is not null)
            {
                if (!MatchesCandidate(terminal.Attempt, candidate, dispatchKind, focusedEvidenceRequest, requestContext))
                {
                    return ReplaceStaleAttempt(
                        terminal.Attempt,
                        candidate,
                        policy,
                        runAcceptance,
                        dispatchKind,
                        focusedEvidenceRequest,
                        requestContext,
                        stableSlotExhaustionPolicy);
                }

                return terminal;
            }

            return ConductorParallelAcceptanceAttemptDecision.Running(current);
        }

        return Launch(
            candidate,
            policy,
            runAcceptance,
            dispatchKind,
            focusedEvidenceRequest,
            requestContext,
            stableSlotExhaustionPolicy);
    }

    private static bool MatchesCandidate(
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceCandidate candidate,
        string dispatchKind,
        string? focusedEvidenceRequest,
        ConductorFocusedEvidenceRequestContext? requestContext) =>
        string.Equals(attempt.CandidateKey, candidate.CandidateKey, StringComparison.Ordinal) &&
        string.Equals(
            string.IsNullOrWhiteSpace(attempt.Kind) ? GateDispatchKind : attempt.Kind,
            dispatchKind,
            StringComparison.Ordinal) &&
        string.Equals(attempt.FocusedEvidenceRequest, focusedEvidenceRequest, StringComparison.Ordinal) &&
        string.Equals(
            attempt.FindingRoundFingerprint,
            requestContext?.FindingRoundFingerprint,
            StringComparison.Ordinal);

    private ConductorParallelAcceptanceAttemptDecision ReplaceStaleAttempt(
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        ConductorParallelAcceptanceRunAcceptance runAcceptance,
        string dispatchKind,
        string? focusedEvidenceRequest,
        ConductorFocusedEvidenceRequestContext? requestContext,
        AcceptanceStableSlotExhaustionPolicy stableSlotExhaustionPolicy)
    {
        var cause = SupersessionCauseFor(
            attempt, candidate, dispatchKind, focusedEvidenceRequest, requestContext);
        MarkStale(attempt, cause);
        if (!string.Equals(dispatchKind, PreReviewEvidenceDispatchKind, StringComparison.Ordinal))
        {
            return Launch(
                candidate,
                policy,
                runAcceptance,
                dispatchKind,
                focusedEvidenceRequest,
                requestContext,
                stableSlotExhaustionPolicy);
        }

        var successor = CreateAttempt(
            candidate,
            policy,
            dispatchKind,
            focusedEvidenceRequest,
            requestContext,
            stableSlotExhaustionPolicy);
        CompleteSupersession(attempt, successor);
        return Launch(
            candidate,
            policy,
            runAcceptance,
            dispatchKind,
            focusedEvidenceRequest,
            requestContext,
            stableSlotExhaustionPolicy,
            successor);
    }

    private static ConductorEvidenceSupersessionCause SupersessionCauseFor(
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceCandidate candidate,
        string dispatchKind,
        string? focusedEvidenceRequest,
        ConductorFocusedEvidenceRequestContext? requestContext)
    {
        if (!string.Equals(attempt.CandidateKey, candidate.CandidateKey, StringComparison.Ordinal))
        {
            return ConductorEvidenceSupersessionCause.CandidateChanged;
        }

        if (!string.Equals(attempt.FocusedEvidenceRequest, focusedEvidenceRequest, StringComparison.Ordinal))
        {
            return ConductorEvidenceSupersessionCause.FocusedRequestChanged;
        }

        if (!string.Equals(
                attempt.FindingRoundFingerprint,
                requestContext?.FindingRoundFingerprint,
                StringComparison.Ordinal))
        {
            return ConductorEvidenceSupersessionCause.FindingRoundChanged;
        }

        return !string.Equals(attempt.Kind, dispatchKind, StringComparison.Ordinal)
            ? ConductorEvidenceSupersessionCause.GoalMovedOn
            : ConductorEvidenceSupersessionCause.CoordinatorReplacement;
    }

    internal void MarkReconciled(ConductorParallelAcceptanceAttempt attempt)
    {
        lock (MetadataWriteGate)
        {
            var current = ReadCanonicalAttempt(attempt.MetadataPath, attempt.GoalId);
            if (!string.Equals(current.AttemptId, attempt.AttemptId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Acceptance attempt identity changed at canonical metadata path '{attempt.MetadataPath}'.");
            }
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

    internal bool RecordFocusedEvidenceRequestDispositions(
        ConductorParallelAcceptanceAttempt attempt,
        string findingRoundFingerprint,
        string receiptId,
        IReadOnlyList<FindingEvidenceRequestDisposition> dispositions)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentException.ThrowIfNullOrWhiteSpace(findingRoundFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(receiptId);
        ArgumentNullException.ThrowIfNull(dispositions);

        ConductorParallelAcceptanceAttempt updated;
        lock (MetadataWriteGate)
        {
            var current = TryReadAttemptFile(attempt.MetadataPath) ?? attempt;
            if (!string.Equals(current.AttemptId, attempt.AttemptId, StringComparison.Ordinal) ||
                !string.Equals(current.FindingRoundFingerprint, findingRoundFingerprint, StringComparison.Ordinal))
            {
                return false;
            }

            updated = current with
            {
                FocusedEvidenceRequestDispositions = dispositions.ToArray(),
                FocusedEvidenceReceiptId = receiptId,
                LastHeartbeatAt = _utcNow()
            };
            WriteAttemptFile(updated);
        }

        if (_conductEventLogWriter is null)
        {
            return true;
        }

        var allRecorded = true;
        foreach (var disposition in dispositions)
        {
            allRecorded &= _conductEventLogWriter.AppendRequired(new ConductEvidenceLifecycleEvent(
                _utcNow(),
                "EVIDENCE_REQUEST_DISPOSITION",
                updated.GoalId,
                updated.GoalId,
                updated.AttemptId,
                updated.Ordinal,
                $"evidence:{updated.AttemptId}:request:{disposition.FindingStableId}:{disposition.Disposition}",
                CandidateSha: updated.BranchHeadSha,
                Policy: updated.PolicyName,
                BatchId: updated.FocusedEvidenceBatchId,
                MemberRequests: updated.FocusedEvidenceMemberRequests,
                RequestDisposition: disposition.Disposition,
                FindingRoundFingerprint: findingRoundFingerprint,
                FindingStableId: disposition.FindingStableId,
                RequestIdentity: disposition.RequestIdentity,
                ReceiptId: receiptId,
                RequestDispositions: [disposition]));
        }

        return allRecorded;
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

    internal IReadOnlyList<ConductorParallelAcceptanceAttempt> GetCapacityReservingAttempts(IEnumerable<string> goalIds) =>
        GetUnreconciledAttempts(goalIds)
            .Where(IsCapacityReservingAttempt)
            .ToArray();

    internal IReadOnlySet<string> GetLiveAttemptGoalIds(IEnumerable<string> goalIds) =>
        GetUnreconciledAttempts(goalIds)
            .Where(attempt => IsLiveAttempt(attempt) || IsLiveInvalidatedAttempt(attempt))
            .Select(attempt => attempt.GoalId)
            .ToHashSet(StringComparer.Ordinal);

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

    private bool IsCapacityReservingAttempt(ConductorParallelAcceptanceAttempt attempt) =>
        attempt switch
        {
            { Outcome: ConductorParallelAcceptanceAttemptOutcome.Running, ReconciledAt: null } =>
                !File.Exists(attempt.ResultPath) &&
                !File.Exists(attempt.ExitCodePath) &&
                _isProcessAlive(attempt.OwnerProcessId),
            { Outcome: ConductorParallelAcceptanceAttemptOutcome.StaleCandidate, ReconciledAt: not null } =>
                IsCapacityReservingInvalidatedAttempt(attempt),
            _ => false
        };

    private bool IsCapacityReservingInvalidatedAttempt(ConductorParallelAcceptanceAttempt attempt) =>
        attempt.Outcome == ConductorParallelAcceptanceAttemptOutcome.StaleCandidate &&
        attempt.ReconciledAt.HasValue &&
        !File.Exists(attempt.ExitCodePath) &&
        _isProcessAlive(attempt.OwnerProcessId);

    internal IReadOnlyList<string> TakePendingLeaseReceipts(ConductorParallelAcceptanceAttempt attempt)
    {
        var goalDirectory = Path.GetDirectoryName(attempt.MetadataPath) ?? _rootDirectory;
        using var artifactLease = StorageRetentionMaintenance.TryAcquireAttemptWriterLease(goalDirectory);
        if (artifactLease is null)
        {
            // Retention may be selecting terminal metadata for deletion. Leave the replay count
            // untouched so a later tick can retry after the cross-process writer boundary clears.
            return [];
        }

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
        ConductorFocusedEvidenceRequestContext? requestContext,
        AcceptanceStableSlotExhaustionPolicy stableSlotExhaustionPolicy,
        ConductorParallelAcceptanceAttempt? reservedAttempt = null)
    {
        var attempt = reservedAttempt ?? CreateAttempt(
            candidate,
            policy,
            dispatchKind,
            focusedEvidenceRequest,
            requestContext,
            stableSlotExhaustionPolicy);
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
                    () => RunAttemptWithArtifactLease(held, candidate, policy, runAcceptance));
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

                    RunAttemptWithArtifactLease(activeAttempt, candidate, policy, runAcceptance);
                },
                _buildStorageRoot));
            var launched = TryPersistOwnerProcess(attempt, launch.ProcessId);
            if (launched.Outcome == ConductorParallelAcceptanceAttemptOutcome.Running)
            {
                WriteHeartbeat(launched, "running");
            }

            return ConductorParallelAcceptanceAttemptDecision.Started(launched);
        }
        catch (ConductorParallelAcceptanceAttemptCompletionGateViolationException ex)
        {
            CompleteLaunchFailure(
                attempt,
                ex,
                $"test completion gate rejected attempt: {ex.Message}{Environment.NewLine}");
            throw;
        }
        catch (Exception ex)
        {
            var failed = CompleteLaunchFailure(
                attempt,
                ex,
                $"launch failed: {ex}{Environment.NewLine}");
            return ConductorParallelAcceptanceAttemptDecision.TerminalWithoutRun(failed);
        }
    }

    internal void RunAttemptForTests(
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        ConductorParallelAcceptanceRunAcceptance runAcceptance)
    {
        RunAttemptWithArtifactLease(attempt, candidate, policy, runAcceptance);
    }

    internal static int RunOwnedProcess(
        string metadataPath,
        TimeSpan? attemptWriterLeaseTimeout = null)
    {
        ConductorParallelAcceptanceAttempt? attempt = null;
        IDisposable? artifactLease = null;
        IReadOnlyDictionary<string, TextWriter>? attemptLogWriters = null;
        try
        {
            attempt = JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(
                ReadAllTextSharedWithRetry(metadataPath),
                JsonOptions);
            if (attempt is null)
            {
                throw new InvalidOperationException("acceptance attempt metadata was empty");
            }

            artifactLease = StorageRetentionMaintenance.AcquireAttemptWriterLease(
                Path.GetDirectoryName(metadataPath) ?? throw new InvalidOperationException(
                    "acceptance attempt metadata path has no parent directory"),
                attemptWriterLeaseTimeout);
            attemptLogWriters = RedirectConsole(attempt);
            var executionDirectory = !string.IsNullOrWhiteSpace(attempt.ExecutionDirectory)
                ? attempt.ExecutionDirectory!
                : OrchestratorWorkspace.ResolveRepoRoot(Environment.CurrentDirectory);
            var workspace = OrchestratorWorkspace.ForDirectory(executionDirectory, executionDirectory);
            var stateRepository = SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath);
            var kernel = stateRepository.LoadGoalsAsync([new GoalId(attempt.GoalId)]).GetAwaiter().GetResult();
            var goal = kernel.Goals.FirstOrDefault(g => g.Id.Value == attempt.GoalId)
                ?? throw new InvalidOperationException($"goal {attempt.GoalPrefix} was not found for acceptance attempt");
            var providers = ProviderRegistryFactory.CreateDefaultProviders();
            var agentFallback = ProviderRegistryFactory.IsLlamaCppReachable() ? AgentCatalog.LlamaCppDefault() : null;
            var agents = AgentCatalogStore.Load(workspace.AgentCatalogPath, agentFallback).Agents;
            var profiles = WorkerProfileStore.Load(workspace.WorkerProfilePath);
            var driver = new ConductorDriver(
                kernel,
                workspace,
                new GoalAcceptanceVerifier(DotnetBuildEnvironmentManager.CaptureStorageRoot()),
                agents,
                profiles,
                NullOperatorChannel.Instance,
                providers,
                cleanupHooks: WorktreeCleanupContext.Load(
                    attentionStoreDirectory: workspace.OrchestratorDirectory).Hooks);
            var policy = ResolveAttemptPolicy(attempt);
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
                    : new ConductEventLogWriter(attempt.ConductEventLogPath),
                attemptLogWriters: attemptLogWriters);
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

                RunPreReviewEvidenceAttempt(
                    coordinator, activeAttempt, candidate, policy,
                    (attemptCandidate, request, lease, runBaselineArm, cancellationToken) =>
                        driver.RunPreReviewBaselineArmFocusedEvidence(
                            attemptCandidate, request, lease, runBaselineArm, cancellationToken));
            }
            else
            {
                var omitStableSlotIndexWithoutLease =
                    activeAttempt.StableSlotExhaustionPolicy ==
                    AcceptanceStableSlotExhaustionPolicy.DegradeToSerial;
                coordinator.RunAttempt(
                    activeAttempt,
                    candidate,
                    policy,
                    (attemptCandidate, attemptPolicy, lease, cancellationToken, executionOptions) =>
                        driver.RunParallelLandingAcceptance(
                            attemptCandidate,
                            attemptPolicy,
                            lease,
                            cancellationToken,
                            executionOptions,
                            omitStableSlotIndexWithoutLease));
            }
            return 0;
        }
        catch (Exception ex)
        {
            if (attempt is not null)
            {
                var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                    Path.GetDirectoryName(Path.GetDirectoryName(attempt.MetadataPath) ?? string.Empty) ?? Environment.CurrentDirectory,
                    attempt.ExecutionDirectory,
                    conductEventLogWriter: string.IsNullOrWhiteSpace(attempt.ConductEventLogPath)
                        ? null
                        : new ConductEventLogWriter(attempt.ConductEventLogPath),
                    attemptLogWriters: attemptLogWriters);
                coordinator.CompleteOwnedProcessFailure(
                    attempt with { OwnerProcessId = Environment.ProcessId },
                    ex);
            }
            else
            {
                Console.Error.WriteLine(ex);
            }

            return 1;
        }
        finally
        {
            artifactLease?.Dispose();
        }
    }

    internal static void RunPreReviewEvidenceAttempt(
        ConductorParallelAcceptanceAttemptCoordinator coordinator,
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        Func<ConductorParallelAcceptanceCandidate, string, DotnetBuildEnvironmentLease?, bool, CancellationToken,
            ConductorParallelAcceptanceRunResult> runner)
    {
        if (!string.Equals(attempt.Kind, PreReviewEvidenceDispatchKind, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(attempt.FocusedEvidenceRequest))
        {
            throw new InvalidOperationException("pre-review evidence attempt metadata did not contain a request");
        }

        var runBaselineArm = attempt.FocusedEvidenceRunsBaselineArm ||
            (attempt.FocusedEvidenceBatchId?.EndsWith("-baseline-arm", StringComparison.Ordinal) ?? false);
        coordinator.RunAttempt(
            attempt, candidate, policy,
            (attemptCandidate, _, lease, cancellationToken, _) =>
                runner(attemptCandidate, attempt.FocusedEvidenceRequest, lease, runBaselineArm, cancellationToken));
    }

    private void RunAttemptWithArtifactLease(
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceCandidate candidate,
        ConductorAutonomyPolicy policy,
        ConductorParallelAcceptanceRunAcceptance runAcceptance)
    {
        using var artifactLease = StorageRetentionMaintenance.AcquireAttemptWriterLease(
            Path.GetDirectoryName(attempt.MetadataPath) ?? throw new InvalidOperationException(
                "acceptance attempt metadata path has no parent directory"));
        RunAttempt(attempt, candidate, policy, runAcceptance);
    }

    internal static ConductorAutonomyPolicy ResolveAttemptPolicy(ConductorParallelAcceptanceAttempt attempt)
    {
        if (!string.IsNullOrWhiteSpace(attempt.PolicyJson))
        {
            try
            {
                return ConductorAutonomyPolicy.ParseJson(attempt.PolicyJson, attempt.MetadataPath);
            }
            catch (FormatException)
            {
                // Preserve compatibility with old or damaged attempt metadata by using the prior name fallback.
            }
        }

        return ConductorAutonomyPolicy.All.FirstOrDefault(candidatePolicy =>
                string.Equals(candidatePolicy.Name, attempt.PolicyName, StringComparison.OrdinalIgnoreCase))
            ?? ConductorAutonomyPolicy.Default;
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
                        _cleanupObservedForTests?.Invoke(attempt, "artifact-custody-released");
                    }
                }
                finally
                {
                    if (stableSlotLease is not null)
                    {
                        stableSlotLease.Dispose();
                        _cleanupObservedForTests?.Invoke(attempt, "stable-slot-released");
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
            _resultPublishedForTests?.Invoke(attempt);
            var outcome = OutcomeFor(run, attempt.Kind);
            var claimedTerminal = TryPersistTerminal(attempt, current => current with
            {
                BranchHeadSha = run.Candidate.BranchHeadSha,
                MainHeadSha = run.Candidate.MainHeadSha,
                Outcome = outcome,
                CompletedAt = _utcNow(),
                LastHeartbeatAt = _utcNow(),
                Detail = AcceptanceRunDetail(run),
                TestResultPaths = ResultTestPaths(run),
                TransientFailureCount = IsBoundedTransientFailure(outcome, AcceptanceRunDetail(run))
                    ? CountConsecutiveTransientFailures(current) + 1
                    : current.TransientFailureCount
            });
            WriteTerminalExitReceipt(
                attempt,
                claimedTerminal,
                outcome == ConductorParallelAcceptanceAttemptOutcome.Passed ? 0 : 1);
            if (!string.IsNullOrWhiteSpace(stderrDetail))
            {
                TryAppendAttemptLog(attempt.StderrPath, $"{stderrDetail}{Environment.NewLine}");
            }

            AppendAttemptLog(attempt.StdoutPath, $"{attempt.Kind} attempt {attempt.AttemptId} completed outcome={outcome}{Environment.NewLine}");
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
            TryAppendAttemptLog(attempt.StderrPath, $"{ex}{Environment.NewLine}");
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
            $"{purpose}-{attempt.AttemptId}",
            storageRoot: _buildStorageRoot);
        var acquisition = DotnetBuildEnvironmentManager.TryAcquireFirstAvailableBuildPermitOwned(
            environment,
            new AcceptanceAttemptArtifactCustodyContext(
                attempt.AttemptId,
                attempt.MetadataPath,
                Environment.ProcessId),
            _buildPermitBusyTimeout,
            onWait: () => EmitAttemptLeaseReceipt(
                "wait",
                attempt,
                candidate,
                holderPid: null,
                permitName: AllBuildPermitNames(),
                waitReason: AcceptanceBuildPermitWaitReason.AllPermitsBusy),
            timeProvider: _timeProvider,
            sleep: _buildPermitSleep);
        if (acquisition is DotnetBuildLeaseAcquisition.Acquired acquired)
        {
            var acquiredAt = _timeProvider.GetTimestamp();
            var permitName = PermitName(acquired.Lease.Environment);
            acquired.Lease.RegisterExecutionLockReleaseObserver(() =>
            {
                var heldMs = Math.Max(
                    0,
                    (long)_timeProvider.GetElapsedTime(acquiredAt, _timeProvider.GetTimestamp()).TotalMilliseconds);
                EmitAttemptPermitReleaseReceipt(
                    attempt,
                    Environment.ProcessId,
                    permitName,
                    heldMs);
            });
            EmitAttemptLeaseReceipt("acquire", attempt, candidate, Environment.ProcessId, permitName);
            EmitAttemptLeaseReceipt("handoff", attempt, candidate, Environment.ProcessId, permitName);
            return acquired.Lease;
        }

        if (acquisition is DotnetBuildLeaseAcquisition.SlotsBusy busy)
        {
            var busyPermits = busy.BusySlots.OrderBy(slot => slot.SlotIndex).ToArray();
            EmitAttemptLeaseReceipt(
                "yield",
                attempt,
                candidate,
                holderPid: null,
                permitName: busyPermits.Length == 0
                    ? AllBuildPermitNames()
                    : BuildPermitNames(busyPermits.Select(slot => slot.SlotIndex)),
                waitReason: AcceptanceBuildPermitWaitReason.AllPermitsBusy,
                holderPidField: BuildPermitHolderPids(busyPermits));
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
        var prefix = Path.Combine(Path.GetDirectoryName(attempt.MetadataPath) ?? Environment.CurrentDirectory, attempt.AttemptId);
        DotnetBuildEnvironmentLease? stableSlotLease;
        try
        {
            stableSlotLease = _acquireStableSlotLease(attempt, candidate);
        }
        catch (Exception ex) when (
            attempt.StableSlotExhaustionPolicy == AcceptanceStableSlotExhaustionPolicy.DegradeToSerial &&
            ex is DotnetBuildSlotsBusyException or BuildLockBlockedException)
        {
            EmitAttemptLeaseReceipt(
                "degrade",
                attempt,
                candidate,
                holderPid: null,
                permitName: AllBuildPermitNames(),
                waitReason: ex is DotnetBuildSlotsBusyException
                    ? AcceptanceBuildPermitWaitReason.AllPermitsBusy
                    : null);
            stableSlotLease = null;
        }
        if (stableSlotLease is not null)
        {
            leaseAcquired(stableSlotLease);
        }
        return runAcceptance(
            candidate,
            policy,
            stableSlotLease,
            CancellationToken.None,
            new AcceptanceRunExecutionOptions(
                RunId: attempt.AttemptId,
                ResultsPrefix: prefix,
                LivenessCheckHint: attempt.MetadataPath));
    }

    private void EmitAttemptLeaseReceipt(
        string action,
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceCandidate candidate,
        int? holderPid,
        string? permitName = null,
        AcceptanceBuildPermitWaitReason? waitReason = null,
        string? holderPidField = null)
    {
        var pid = holderPidField ??
            holderPid?.ToString(System.Globalization.CultureInfo.InvariantCulture) ??
            "unknown";
        var slot = permitName ?? $"acceptance-{candidate.SlotIndex}";
        var waitReasonField = waitReason is null ? string.Empty : $" waitReason={WaitReasonName(waitReason.Value)}";
        var line = $"ACCEPTANCE_LEASE_{action.ToUpperInvariant()} goal={attempt.GoalPrefix} attempt={attempt.AttemptId} permit={slot} holderPid={pid}{waitReasonField}";
        Console.WriteLine(line);
        PersistLeaseReceipt(attempt, line, waitReason);
    }

    private void EmitAttemptPermitReleaseReceipt(
        ConductorParallelAcceptanceAttempt attempt,
        int holderPid,
        string permitName,
        long heldMs)
    {
        var line =
            $"ACCEPTANCE_LEASE_PERMIT_RELEASE goal={attempt.GoalPrefix} attempt={attempt.AttemptId} " +
            $"permit={permitName} holderPid={holderPid.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
            $"releaseKind=execution-lock heldMs={heldMs.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        Console.WriteLine(line);
        PersistLeaseReceipt(attempt, line, waitReason: null);
    }

    private static string PermitName(DotnetBuildEnvironment environment) =>
        environment.BuildPermitIndex is { } permitIndex
            ? $"build-{permitIndex}"
            : Path.GetFileNameWithoutExtension(environment.ExecutionLockPath);

    private static string AllBuildPermitNames() =>
        BuildPermitNames(Enumerable.Range(0, DotnetBuildEnvironmentManager.BuildConcurrencySlotCount));

    private static string BuildPermitNames(IEnumerable<int> permitIndexes) =>
        string.Join("|", permitIndexes.Select(index => $"build-{index}"));

    private static string BuildPermitHolderPids(IEnumerable<DotnetBuildStableSlotWait> busyPermits) =>
        string.Join(
            "|",
            busyPermits.Select(permit =>
                permit.OwnerProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"));

    private static string WaitReasonName(AcceptanceBuildPermitWaitReason waitReason) =>
        waitReason switch
        {
            AcceptanceBuildPermitWaitReason.AllPermitsBusy => "all-permits-busy",
            AcceptanceBuildPermitWaitReason.DesignatedPermitBusyWhileFree => "designated-permit-busy-while-free",
            _ => throw new ArgumentOutOfRangeException(nameof(waitReason), waitReason, "Unknown acceptance build permit wait reason.")
        };

    private void PersistLeaseReceipt(
        ConductorParallelAcceptanceAttempt attempt,
        string line,
        AcceptanceBuildPermitWaitReason? waitReason)
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
                BuildPermitWaitReason = waitReason ?? current.BuildPermitWaitReason,
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
        var claimedTerminal = TryPersistTerminal(attempt, current => current with
        {
            Outcome = outcome,
            CompletedAt = _utcNow(),
            LastHeartbeatAt = _utcNow(),
            Detail = detail,
            TransientFailureCount = transient
                ? CountConsecutiveTransientFailures(current) + 1
                : current.TransientFailureCount
        });
        WriteTerminalExitReceipt(attempt, claimedTerminal, claimedExitCode: 1);
        TryAppendAttemptLog(attempt.StderrPath, $"{outcome}: {detail}{Environment.NewLine}");
        WriteHeartbeat(attempt, "exiting");
    }

    private void CompleteOwnedProcessFailure(
        ConductorParallelAcceptanceAttempt attempt,
        Exception exception)
    {
        var transient = IsTransientAttemptIo(exception);
        TryAppendAttemptLog(attempt.StderrPath, $"{exception}{Environment.NewLine}");
        CompleteWithoutResult(
            attempt,
            exception is TimeoutException
                ? ConductorParallelAcceptanceAttemptOutcome.InfrastructureDeferred
                : transient
                    ? ConductorParallelAcceptanceAttemptOutcome.CorruptArtifacts
                    : ConductorParallelAcceptanceAttemptOutcome.Failed,
            exception.Message,
            transient: transient);
    }

    internal void CompleteOwnedProcessFailureForTests(
        ConductorParallelAcceptanceAttempt attempt,
        Exception exception) =>
        CompleteOwnedProcessFailure(attempt, exception);

    private ConductorParallelAcceptanceAttempt CompleteLaunchFailure(
        ConductorParallelAcceptanceAttempt attempt,
        Exception exception,
        string stderrDetail)
    {
        var transient = IsTransientAttemptIo(exception);
        var claimedTerminal = TryPersistTerminal(attempt, current => current with
        {
            Outcome = ConductorParallelAcceptanceAttemptOutcome.LaunchFailed,
            CompletedAt = _utcNow(),
            LastHeartbeatAt = _utcNow(),
            Detail = exception.Message,
            TransientFailureCount = transient
                ? CountConsecutiveTransientFailures(current) + 1
                : current.TransientFailureCount
        });
        TryAppendAttemptLog(attempt.StderrPath, stderrDetail);
        WriteTerminalExitReceipt(attempt, claimedTerminal, claimedExitCode: 1);
        return TryReadAttemptFile(attempt.MetadataPath) ?? attempt with
        {
            Outcome = ConductorParallelAcceptanceAttemptOutcome.LaunchFailed,
            CompletedAt = _utcNow(),
            LastHeartbeatAt = _utcNow(),
            Detail = exception.Message
        };
    }

    internal ConductorParallelAcceptanceAttempt CompleteLaunchFailureForTests(
        ConductorParallelAcceptanceAttempt attempt,
        Exception exception) =>
        CompleteLaunchFailure(attempt, exception, $"launch failed: {exception}{Environment.NewLine}");

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
        var latest = (TryReadAttemptFile(attempt.MetadataPath) ?? attempt) with
        {
            MetadataPath = attempt.MetadataPath
        };
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
            var current = (TryReadAttemptFile(attempt.MetadataPath) ?? attempt) with
            {
                MetadataPath = attempt.MetadataPath
            };
            if (!string.Equals(current.AttemptId, attempt.AttemptId, StringComparison.Ordinal))
            {
                return current;
            }

            var outcome = current.Outcome == ConductorParallelAcceptanceAttemptOutcome.Running
                ? OutcomeFor(run, current.Kind)
                : current.Outcome;
            var updated = current with
            {
                BranchHeadSha = run.Candidate.BranchHeadSha,
                MainHeadSha = run.Candidate.MainHeadSha,
                Outcome = outcome,
                CompletedAt = current.CompletedAt ?? _utcNow(),
                LastHeartbeatAt = _utcNow(),
                Detail = current.Detail ?? AcceptanceRunDetail(run),
                TestResultPaths = ResultTestPaths(run) ?? current.TestResultPaths,
                TransientFailureCount = IsBoundedTransientFailure(outcome, current.Detail ?? AcceptanceRunDetail(run))
                    ? current.TransientFailureCount > 0
                        ? current.TransientFailureCount
                        : CountConsecutiveTransientFailures(current) + 1
                    : current.TransientFailureCount
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
        string? focusedEvidenceRequest,
        ConductorFocusedEvidenceRequestContext? requestContext,
        AcceptanceStableSlotExhaustionPolicy stableSlotExhaustionPolicy)
    {
        var startedAt = _utcNow();
        var rawId = $"{candidate.GoalPrefix}-{candidate.SlotIndex}-{startedAt:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        var id = rawId[..Math.Min(64, rawId.Length)];
        var directory = Path.Combine(_rootDirectory, candidate.Goal.Id.Value);
        Directory.CreateDirectory(directory);
        var ordinal = AllocateOrdinal(directory);
        var prefix = Path.Combine(directory, id);
        var focusedMembers = focusedEvidenceRequest?
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var focusedBatchId = requestContext?.BatchId ?? (focusedMembers is { Length: > 0 }
            ? "evidence-batch-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{candidate.Goal.Id.Value}:{candidate.BranchHeadSha}:{focusedEvidenceRequest}")))
                .ToLowerInvariant()[..16]
            : null);
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
            ConductEventLogPath: _conductEventLogWriter?.CurrentPath,
            FocusedEvidenceBatchId: focusedBatchId,
            FocusedEvidenceRunsBaselineArm: requestContext?.RunBaselineArm ?? false,
            CandidateEvidenceBeforeBaseline: requestContext?.CandidateEvidenceBeforeBaseline,
            FocusedEvidenceMemberRequests: focusedMembers,
            FocusedEvidenceRequestDisposition: requestContext?.RequestDispositions
                .Select(disposition => disposition.Disposition)
                .FirstOrDefault(disposition => disposition.StartsWith("executed-", StringComparison.Ordinal)),
            FindingRoundFingerprint: requestContext?.FindingRoundFingerprint,
            FocusedEvidenceRequestDispositions: requestContext?.RequestDispositions,
            PolicyJson: policy.ToJson(),
            StableSlotExhaustionPolicy: stableSlotExhaustionPolicy,
            ExecutionProtocol: _runInline ? "in-process" : "out-of-process",
            ConductorGenerationId: _conductorGenerationId);
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
        ConductorParallelAcceptanceAttempt? latestUnreconciled = null;
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

            // The directory enumeration is the registration authority. Metadata may be stale,
            // corrupt, or attacker-controlled; never let its serialized path redirect later writes.
            attempt = attempt with { MetadataPath = Path.GetFullPath(path) };

            latest = latest is null || attempt.StartedAt > latest.StartedAt ? attempt : latest;
            if (!IsReconciled(attempt) &&
                (latestUnreconciled is null || attempt.StartedAt > latestUnreconciled.StartedAt))
            {
                latestUnreconciled = attempt;
            }
        }

        return latestUnreconciled ?? latest;
    }

    private ConductorParallelAcceptanceAttempt ReadCanonicalAttempt(string path, string expectedGoalId)
    {
        var canonicalPath = Path.GetFullPath(path);
        var expectedDirectory = Path.GetFullPath(Path.Combine(_rootDirectory, expectedGoalId));
        if (!string.Equals(Path.GetDirectoryName(canonicalPath), expectedDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Acceptance attempt metadata path '{canonicalPath}' is outside canonical goal directory '{expectedDirectory}'.");
        }

        var attempt = TryReadAttemptFile(canonicalPath) ??
            throw new InvalidDataException($"Acceptance attempt metadata '{canonicalPath}' is unreadable.");
        var expectedFileName = $"{attempt.AttemptId}.attempt.json";
        if (!string.Equals(Path.GetFileName(canonicalPath), expectedFileName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Acceptance attempt metadata path '{canonicalPath}' is not canonical for attempt '{attempt.AttemptId}'.");
        }

        if (!string.Equals(attempt.GoalId, expectedGoalId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Acceptance attempt '{attempt.AttemptId}' belongs to goal '{attempt.GoalId}', not canonical directory goal '{expectedGoalId}'.");
        }

        return attempt with { MetadataPath = canonicalPath };
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
                TryAppendAttemptLog(attempt.StderrPath, $"terminal outcome ignored because durable attempt is no longer running{Environment.NewLine}");
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
            $"evidence:{attempt.AttemptId}:start",
            CandidateSha: attempt.BranchHeadSha,
            Policy: attempt.PolicyName,
            BatchId: attempt.FocusedEvidenceBatchId,
            MemberRequests: attempt.FocusedEvidenceMemberRequests,
            RequestDisposition: attempt.FocusedEvidenceRequestDisposition,
            FindingRoundFingerprint: attempt.FindingRoundFingerprint,
            ReceiptId: attempt.FocusedEvidenceReceiptId,
            RequestDispositions: attempt.FocusedEvidenceRequestDispositions));
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
            attempt.Detail,
            CandidateSha: attempt.BranchHeadSha,
            Policy: attempt.PolicyName,
            BatchId: attempt.FocusedEvidenceBatchId,
            MemberRequests: attempt.FocusedEvidenceMemberRequests,
            RequestDisposition: attempt.FocusedEvidenceRequestDisposition,
            FindingRoundFingerprint: attempt.FindingRoundFingerprint,
            ReceiptId: attempt.FocusedEvidenceReceiptId,
            RequestDispositions: attempt.FocusedEvidenceRequestDispositions));
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
            ConductorParallelAcceptanceAttemptOutcome.InfrastructureDeferred => ConductorEvidenceAttemptOutcome.InfrastructureDeferred,
            ConductorParallelAcceptanceAttemptOutcome.StructuralCoveragePermitUnavailable => ConductorEvidenceAttemptOutcome.StructuralCoveragePermitUnavailable,
            ConductorParallelAcceptanceAttemptOutcome.GateEngineFault => ConductorEvidenceAttemptOutcome.GateEngineFault,
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

    /// <summary>
    /// Adopts an orphan gate attempt left running by another conductor generation, returning the adopted
    /// attempt, or null when the attempt is not an orphan of a dead generation. Adoption refreshes the
    /// heartbeat every tick so the attempt does not read stale to any other observer, and records the
    /// adopting generation at most once, best-effort, for the reason given below.
    /// </summary>
    private ConductorParallelAcceptanceAttempt? TryAdoptOrphanAttempt(ConductorParallelAcceptanceAttempt attempt)
    {
        if (!ConductorOrphanGateAttemptAdoption.ShouldAdopt(
                attempt,
                _conductorGenerationId,
                _isProcessAlive,
                File.Exists))
        {
            return null;
        }

        var adopted = ConductorOrphanGateAttemptAdoption.NeedsAdoptionRecord(attempt, _conductorGenerationId)
            ? TryPersistAdoption(attempt) ?? attempt
            : attempt;
        WriteHeartbeat(adopted, "adopted");
        return adopted;
    }

    /// <summary>
    /// Records the adopting generation on the attempt, or returns null when the writer lease is held.
    ///
    /// The running gate child holds that lease for its whole run, and the in-process metadata gate cannot
    /// order a write against another process. The adoption record is evidence, not the fence — the fence
    /// is the decision above, which does not depend on it — so a busy lease skips the record and retries
    /// on a later tick rather than racing the child's terminal write and clobbering a published verdict.
    /// </summary>
    private ConductorParallelAcceptanceAttempt? TryPersistAdoption(ConductorParallelAcceptanceAttempt attempt)
    {
        var goalDirectory = Path.GetDirectoryName(attempt.MetadataPath);
        if (string.IsNullOrWhiteSpace(goalDirectory))
        {
            return null;
        }

        using var artifactLease = StorageRetentionMaintenance.TryAcquireAttemptWriterLease(goalDirectory);
        if (artifactLease is null)
        {
            return null;
        }

        lock (MetadataWriteGate)
        {
            var current = TryReadAttemptFile(attempt.MetadataPath);
            if (current is null ||
                !string.Equals(current.AttemptId, attempt.AttemptId, StringComparison.Ordinal) ||
                current.Outcome != ConductorParallelAcceptanceAttemptOutcome.Running)
            {
                return null;
            }

            var updated = current with
            {
                AdoptedByGenerationId = _conductorGenerationId,
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
        var startInfo = BuildOwnedProcessStartInfo(launch.Attempt, buildStorageRoot: launch.BuildStorageRoot);
        var process = ProcessTreeGuiSuppression.Start(startInfo)
            ?? throw new InvalidOperationException("failed to start acceptance attempt process");
        var processId = process.Id;
        DetachOwnedProcessStreams(process, launch.Attempt);
        return new ConductorParallelAcceptanceOwnedProcessLaunchResult(processId);
    }

    internal static ProcessStartInfo BuildOwnedProcessStartInfo(
        ConductorParallelAcceptanceAttempt attempt,
        DotnetBuildStorageRoot? buildStorageRoot,
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
        if (buildStorageRoot is { } storageRoot)
        {
            // This attempt selects the namespace; the child and its build descendants
            // need the same value after ambient verification overrides are scrubbed.
            startInfo.Environment[DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable] = storageRoot.RootPath;
        }
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

    private static IReadOnlyDictionary<string, TextWriter> RedirectConsole(ConductorParallelAcceptanceAttempt attempt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(attempt.StdoutPath) ?? ".");
        var stdout = new StreamWriter(new FileStream(attempt.StdoutPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        var stderr = new StreamWriter(new FileStream(attempt.StderrPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        Console.SetOut(stdout);
        Console.SetError(stderr);
        return new Dictionary<string, TextWriter>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.GetFullPath(attempt.StdoutPath)] = Console.Out,
            [Path.GetFullPath(attempt.StderrPath)] = Console.Error
        };
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
                    AcceptanceInfrastructureDeferredException deferred when
                        deferred.ReasonCode == "structural-coverage-permit-unavailable" =>
                        "structural-coverage-permit-unavailable",
                    AcceptanceInfrastructureDeferredException => "infrastructure-deferred",
                    AcceptanceGateEngineException => "gate-engine-fault",
                    OperationCanceledException => "cancelled",
                    _ => "exception"
                },
                WorkerRegistrationFaultMessage(run.Exception) ?? run.Exception.Message,
                null,
                null,
                null,
                run.Candidate.BranchHeadSha,
                run.Candidate.MainHeadSha,
                null,
                null,
                InfrastructureReasonCode: (run.Exception as AcceptanceInfrastructureDeferredException)?.ReasonCode,
                InfrastructureExitCode: (run.Exception as AcceptanceInfrastructureDeferredException)?.ExitCode,
                InfrastructureOutputTail: (run.Exception as AcceptanceInfrastructureDeferredException)?.OutputTail,
                InfrastructureBuildLockAttribution: (run.Exception as AcceptanceInfrastructureDeferredException)?.BuildLockAttribution,
                FaultType: (run.Exception as AcceptanceGateEngineException)?.FaultType,
                FaultStack: (run.Exception as AcceptanceGateEngineException)?.FaultStack,
                GatePhase: (run.Exception as AcceptanceGateEngineException)?.GatePhase,
                GateTarget: (run.Exception as AcceptanceGateEngineException)?.GateTarget);
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
            "structural-coverage-permit-unavailable" when
                artifact.InfrastructureReasonCode == "structural-coverage-permit-unavailable" =>
                new AcceptanceInfrastructureDeferredException(
                    artifact.InfrastructureReasonCode,
                    artifact.InfrastructureExitCode,
                    artifact.InfrastructureOutputTail ?? artifact.FaultMessage,
                    artifact.InfrastructureBuildLockAttribution),
            "structural-coverage-permit-unavailable" =>
                throw new InvalidDataException("Structural coverage permit fault has inconsistent reason code."),
            "infrastructure-deferred" => new AcceptanceInfrastructureDeferredException(
                artifact.InfrastructureReasonCode ?? "background-acceptance-infrastructure-unavailable",
                artifact.InfrastructureExitCode,
                artifact.InfrastructureOutputTail ?? artifact.FaultMessage,
                artifact.InfrastructureBuildLockAttribution),
            "gate-engine-fault" => AcceptanceGateEngineException.Rehydrate(
                artifact.FaultMessage,
                artifact.FaultType,
                artifact.FaultStack,
                artifact.GatePhase,
                artifact.GateTarget),
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

        if (run.Exception is AcceptanceInfrastructureDeferredException
            { ReasonCode: "structural-coverage-permit-unavailable" })
        {
            return ConductorParallelAcceptanceAttemptOutcome.StructuralCoveragePermitUnavailable;
        }

        if (run.Exception is AcceptanceInfrastructureDeferredException)
        {
            return ConductorParallelAcceptanceAttemptOutcome.InfrastructureDeferred;
        }

        if (run.Exception is AcceptanceGateEngineException)
        {
            return ConductorParallelAcceptanceAttemptOutcome.GateEngineFault;
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
            or ConductorParallelAcceptanceAttemptOutcome.InfrastructureDeferred
            or ConductorParallelAcceptanceAttemptOutcome.StructuralCoveragePermitUnavailable
            or ConductorParallelAcceptanceAttemptOutcome.LaunchFailed;

    private static bool IsReconciled(ConductorParallelAcceptanceAttempt attempt) =>
        attempt.ReconciledAt.HasValue ||
        attempt.Outcome == ConductorParallelAcceptanceAttemptOutcome.Reconciled;

    private static string AcceptanceRunDetail(ConductorParallelAcceptanceRunResult run)
    {
        if (run.Exception is not null)
        {
            return WorkerRegistrationFaultMessage(run.Exception) ?? run.Exception.Message;
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

    internal ConductorParallelAcceptanceAttempt CreateAttemptForTests(
        ConductorParallelAcceptanceCandidate candidate)
    {
        var attempt = CreateAttempt(
            candidate,
            ConductorAutonomyPolicy.Permissive,
            GateDispatchKind,
            focusedEvidenceRequest: null,
            requestContext: null,
            AcceptanceStableSlotExhaustionPolicy.Fail);
        Persist(attempt);
        return attempt;
    }

    private static ConductorParallelAcceptanceAttempt? TryReadAttemptFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var attempt = JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(
                ReadAllTextSharedWithRetry(path),
                JsonOptions);
            return attempt is null
                ? null
                : attempt with { MetadataPath = Path.GetFullPath(path) };
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

    private static void WriteTerminalExitReceipt(
        ConductorParallelAcceptanceAttempt attempt,
        bool claimedTerminal,
        int claimedExitCode)
    {
        if (claimedTerminal)
        {
            TryWriteExit(attempt.ExitCodePath, claimedExitCode);
            return;
        }

        if (File.Exists(attempt.ExitCodePath))
        {
            return;
        }

        // Terminal metadata and the attempt exit receipt have different owners. Losing the metadata
        // claim preserves the parent's decision, but does not change this completion path's exit claim.
        TryWriteExit(attempt.ExitCodePath, claimedExitCode);
    }

    private void AppendAttemptLog(string path, string text)
    {
        if (_attemptLogWriters is not null &&
            _attemptLogWriters.TryGetValue(Path.GetFullPath(path), out var writer))
        {
            writer.Write(text);
            writer.Flush();
            return;
        }

        File.AppendAllText(path, text);
    }

    private void TryAppendAttemptLog(string path, string text)
    {
        try { AppendAttemptLog(path, text); }
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
            attempt.Outcome == ConductorParallelAcceptanceAttemptOutcome.ProcessDied ||
            IsBoundedTransientFailure(attempt.Outcome, attempt.Detail));

    internal static bool IsBoundedInfrastructureOutcome(ConductorParallelAcceptanceAttemptOutcome outcome) =>
        outcome is ConductorParallelAcceptanceAttemptOutcome.BlockedBuildLock or
            ConductorParallelAcceptanceAttemptOutcome.InfrastructureDeferred or
            ConductorParallelAcceptanceAttemptOutcome.StructuralCoveragePermitUnavailable or
            ConductorParallelAcceptanceAttemptOutcome.GateEngineFault;

    internal static WorkerRegistrationFaultDisposition ClassifyWorkerRegistrationFault(Exception? exception)
    {
        var message = WorkerRegistrationFaultMessage(exception);
        return message is null
            ? WorkerRegistrationFaultDisposition.None
            : ClassifyWorkerRegistrationFault(message);
    }

    internal static string? WorkerRegistrationFaultMessage(Exception? exception)
    {
        const int MaxInnerExceptionDepth = 8;
        for (var depth = 0; exception is not null && depth < MaxInnerExceptionDepth; depth++, exception = exception.InnerException)
        {
            var disposition = ClassifyWorkerRegistrationFault(exception.Message);
            if (disposition != WorkerRegistrationFaultDisposition.None)
            {
                return exception.Message.Trim();
            }
        }

        return null;
    }

    internal static WorkerRegistrationFaultDisposition ClassifyWorkerRegistrationFault(string? message)
    {
        const string Prefix = "worker-process-registration-failed";
        var trimmed = message?.Trim();
        if (string.IsNullOrEmpty(trimmed) ||
            !trimmed.StartsWith(Prefix, StringComparison.Ordinal) ||
            trimmed.Length <= Prefix.Length ||
            trimmed[Prefix.Length] is not (':' or ';'))
        {
            return WorkerRegistrationFaultDisposition.None;
        }

        var stage = trimmed[(Prefix.Length + 1)..]
            .Split(';', StringSplitOptions.TrimEntries)
            .FirstOrDefault(segment => segment.StartsWith("stage=", StringComparison.Ordinal));
        if (stage is null)
        {
            return WorkerRegistrationFaultDisposition.Terminal;
        }

        return stage["stage=".Length..].Trim() switch
        {
            "duplicate-or-recycled-pid" or
            "owned-process-group-attachment" or
            "victim-identity-read" or
            "owner-identity-read" or
            "job-publication" or
            "process-resume" or
            "durable-registry-write" => WorkerRegistrationFaultDisposition.BoundedRetry,
            "protected-process-boundary" => WorkerRegistrationFaultDisposition.Terminal,
            _ => WorkerRegistrationFaultDisposition.Terminal
        };
    }

    private static bool IsBoundedTransientFailure(
        ConductorParallelAcceptanceAttemptOutcome outcome,
        string? detail) =>
        IsBoundedInfrastructureOutcome(outcome) ||
        outcome == ConductorParallelAcceptanceAttemptOutcome.Failed &&
            ClassifyWorkerRegistrationFault(detail) == WorkerRegistrationFaultDisposition.BoundedRetry;

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
        (ex is IOException or UnauthorizedAccessException or TimeoutException ||
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
    FocusedEvidenceRunResult? FocusedEvidence = null,
    string? InfrastructureReasonCode = null,
    int? InfrastructureExitCode = null,
    string? InfrastructureOutputTail = null,
    BuildLockAttribution? InfrastructureBuildLockAttribution = null,
    string? FaultType = null,
    string? FaultStack = null,
    string? GatePhase = null,
    string? GateTarget = null);
