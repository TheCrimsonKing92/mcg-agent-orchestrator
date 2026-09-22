using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum GoalEvidenceLeaseRecoveryStatus
{
    Live,
    TerminalReclaimPending,
    Reclaimed,
    StateUnavailable
}

internal static class GoalEvidenceLeaseRecoveryStatuses
{
    public static string Format(GoalEvidenceLeaseRecoveryStatus status) => status switch
    {
        GoalEvidenceLeaseRecoveryStatus.Live => "live",
        GoalEvidenceLeaseRecoveryStatus.TerminalReclaimPending => "terminal-reclaim-pending",
        GoalEvidenceLeaseRecoveryStatus.Reclaimed => "reclaimed",
        _ => "state-unavailable"
    };
}

internal sealed record GoalEvidenceOperationOwner(
    string Operation,
    int ProcessId,
    string InstanceId)
{
    private const string Prefix = "goal-evidence:";
    private const string ProtectedPrefix = $"{Prefix}v1:";

    public string Serialize() => $"{ProtectedPrefix}{Operation}:{ProcessId}:{InstanceId}";

    public static bool IsGoalEvidence(string owner) =>
        owner.StartsWith(Prefix, StringComparison.Ordinal);

    public static bool IsProtected(string owner) =>
        owner.StartsWith(ProtectedPrefix, StringComparison.Ordinal);

    public static bool TryParse(string? value, out GoalEvidenceOperationOwner? owner)
    {
        owner = null;
        if (string.IsNullOrWhiteSpace(value) || !IsProtected(value))
        {
            return false;
        }

        var parts = value[ProtectedPrefix.Length..].Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length < 3 ||
            !int.TryParse(parts[^2], out var processId) ||
            processId < 1 ||
            string.IsNullOrWhiteSpace(parts[^1]))
        {
            return false;
        }

        var operation = string.Join(':', parts[..^2]);
        if (string.IsNullOrWhiteSpace(operation))
        {
            return false;
        }

        owner = new GoalEvidenceOperationOwner(operation, processId, parts[^1]);
        return true;
    }
}

internal sealed record GoalEvidenceLeaseFact(
    string GoalId,
    string? Owner,
    string? Operation,
    string? OperationInstanceId,
    DateTimeOffset? AcquiredAtUtc,
    GoalEvidenceLeaseRecoveryStatus RecoveryStatus,
    string? LatestRecovery = null);

internal sealed record GoalEvidenceOperationStart(
    GoalEvidenceOperationScope? Scope,
    GoalEvidenceLeaseFact? LeaseFact)
{
    public bool Acquired => Scope is not null;
}

internal sealed class GoalEvidenceOperationCoordinator
{
    internal const string BeginFailedLeaseRecovery = "begin-failed";
    internal const string TerminalFailedLeaseRecovery = "terminal-failed";
    private static readonly JsonSerializerOptions RecoveryJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IReconcileSweepRemediationStore _store;
    private readonly string _executionDirectory;
    private readonly TimeSpan _leaseDuration;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<int> _processId;
    private readonly Func<string> _instanceId;
    private readonly Func<DateTimeOffset> _processStartedAtUtc;
    private readonly IConductLockPidProbe? _pidProbe;

    public GoalEvidenceOperationCoordinator(
        IReconcileSweepRemediationStore store,
        string executionDirectory,
        TimeSpan leaseDuration,
        Func<DateTimeOffset>? utcNow = null,
        Func<int>? processId = null,
        Func<string>? instanceId = null,
        Func<DateTimeOffset>? processStartedAtUtc = null,
        IConductLockPidProbe? pidProbe = null)
    {
        _store = store;
        _executionDirectory = Path.GetFullPath(executionDirectory);
        _leaseDuration = leaseDuration;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _processId = processId ?? (() => Environment.ProcessId);
        _instanceId = instanceId ?? (() => Guid.NewGuid().ToString("N"));
        _processStartedAtUtc = processStartedAtUtc ?? CurrentProcessStartedAtUtc;
        _pidProbe = pidProbe;
    }

    public GoalEvidenceOperationStart TryBegin(Goal goal, string operation)
    {
        var operationOwner = new GoalEvidenceOperationOwner(operation, _processId(), _instanceId());
        var serializedOwner = operationOwner.Serialize();
        var recovery = (string?)null;

        if (!_store.TryClaimAcceptanceLease(goal.Id.Value, serializedOwner, _leaseDuration))
        {
            var current = ReadFact(goal);
            if (current is null ||
                current.RecoveryStatus != GoalEvidenceLeaseRecoveryStatus.TerminalReclaimPending ||
                string.IsNullOrWhiteSpace(current.Owner) ||
                !_store.TryReplaceAcceptanceLease(goal.Id.Value, current.Owner, serializedOwner))
            {
                return new GoalEvidenceOperationStart(null, current ?? StateUnavailable(goal.Id.Value));
            }

            recovery = $"reclaimed:{current.OperationInstanceId}";
        }

        try
        {
            GoalOperationJournal.Begin(
                _executionDirectory,
                goal,
                operation,
                "Goal-evidence operation acquired its serialized lease.",
                operationInstanceId: operationOwner.InstanceId,
                leaseRecovery: recovery,
                ownerProcessStartedAtUtc: _processStartedAtUtc().ToUniversalTime());
        }
        catch (Exception beginFailure)
        {
            // A structured lease without its exact Begin evidence can never be reclaimed safely.
            // Persist recovery independently from the failed operation journal before attempting
            // owner-qualified rollback. If SQLite release also fails, a later tick can still reclaim
            // this exact owner without relying on age or process liveness.
            Exception? recoveryFailure = null;
            try
            {
                RecordRecovery(
                    _executionDirectory,
                    goal.Id,
                    operationOwner,
                    serializedOwner,
                    _utcNow(),
                    BeginFailedLeaseRecovery);
            }
            catch (Exception ex)
            {
                recoveryFailure = ex;
            }

            try
            {
                _store.ReleaseAcceptanceLease(goal.Id.Value, serializedOwner);
            }
            catch (Exception releaseFailure)
            {
                if (recoveryFailure is not null)
                {
                    throw new AggregateException(
                        "Goal-evidence Begin, compensating release, and durable recovery recording all failed.",
                        beginFailure,
                        releaseFailure,
                        recoveryFailure);
                }
            }

            ExceptionDispatchInfo.Capture(beginFailure).Throw();
            throw;
        }
        return new GoalEvidenceOperationStart(
            new GoalEvidenceOperationScope(
                _store,
                _executionDirectory,
                goal,
                operationOwner,
                serializedOwner,
                recovery,
                _utcNow),
            new GoalEvidenceLeaseFact(
                goal.Id.Value,
                serializedOwner,
                operation,
                operationOwner.InstanceId,
                _utcNow(),
                recovery is null
                    ? GoalEvidenceLeaseRecoveryStatus.Live
                    : GoalEvidenceLeaseRecoveryStatus.Reclaimed,
                recovery));
    }

    public GoalEvidenceLeaseFact? ReadFact(Goal goal)
    {
        ReconcileAcceptanceLeaseState? lease;
        try
        {
            lease = _store.TryGetAcceptanceLease(goal.Id.Value, _leaseDuration);
        }
        catch
        {
            return StateUnavailable(goal.Id.Value);
        }

        if (lease is null)
        {
            return null;
        }

        return Classify(_executionDirectory, goal, lease, _pidProbe);
    }

    public GoalEvidenceLeaseFact? TryRecoverTerminal(Goal goal)
    {
        var fact = ReadFact(goal);
        if (fact?.RecoveryStatus != GoalEvidenceLeaseRecoveryStatus.TerminalReclaimPending ||
            string.IsNullOrWhiteSpace(fact.Owner))
        {
            return fact;
        }

        try
        {
            _store.ReleaseAcceptanceLease(goal.Id.Value, fact.Owner);
            return fact with
            {
                RecoveryStatus = GoalEvidenceLeaseRecoveryStatus.Reclaimed,
                LatestRecovery = $"reclaimed:{fact.OperationInstanceId}"
            };
        }
        catch
        {
            return fact;
        }
    }

    internal static GoalEvidenceLeaseFact Classify(
        string executionDirectory,
        Goal goal,
        ReconcileAcceptanceLeaseState lease,
        IConductLockPidProbe? pidProbe = null)
    {

        if (!GoalEvidenceOperationOwner.TryParse(lease.Owner, out var owner) || owner is null)
        {
            return new GoalEvidenceLeaseFact(
                goal.Id.Value,
                lease.Owner,
                null,
                null,
                lease.AcquiredAtUtc,
                GoalEvidenceLeaseRecoveryStatus.StateUnavailable);
        }

        try
        {
            var independentRecovery = ReadRecovery(executionDirectory, goal.Id, owner, lease.Owner);
            if (independentRecovery is not null)
            {
                return new GoalEvidenceLeaseFact(
                    goal.Id.Value,
                    lease.Owner,
                    owner.Operation,
                    owner.InstanceId,
                    lease.AcquiredAtUtc,
                    GoalEvidenceLeaseRecoveryStatus.TerminalReclaimPending,
                    independentRecovery);
            }

            var summary = GoalOperationJournal.ReadStrict(executionDirectory, goal.Id);
            var entries = summary.Entries
                .Where(entry =>
                    string.Equals(entry.Operation, owner.Operation, StringComparison.Ordinal) &&
                    string.Equals(entry.OperationInstanceId, owner.InstanceId, StringComparison.Ordinal))
                .ToArray();
            var begins = entries.Count(entry => entry.Status == GoalOperationStatus.Begin);
            var terminals = entries
                .Where(entry => entry.Status is GoalOperationStatus.Completed or GoalOperationStatus.Failed or
                    GoalOperationStatus.Aborted or GoalOperationStatus.Skipped)
                .ToArray();
            var failedBeginIsRecoverable =
                begins == 0 &&
                terminals.Length == 1 &&
                entries.Length == 1 &&
                terminals[0].Status == GoalOperationStatus.Aborted &&
                string.Equals(terminals[0].LeaseRecovery, BeginFailedLeaseRecovery, StringComparison.Ordinal);
            var beginOnly = begins == 1 && terminals.Length == 0 && entries.Length == 1
                ? entries[0]
                : null;
            var status = beginOnly is not null
                ? ClassifyBeginOnlyOwner(owner, beginOnly, pidProbe)
                : begins == 1 && terminals.Length == 1 && ReferenceEquals(entries[^1], terminals[0])
                    ? GoalEvidenceLeaseRecoveryStatus.TerminalReclaimPending
                    : failedBeginIsRecoverable
                        ? GoalEvidenceLeaseRecoveryStatus.TerminalReclaimPending
                        : GoalEvidenceLeaseRecoveryStatus.StateUnavailable;
            return new GoalEvidenceLeaseFact(
                goal.Id.Value,
                lease.Owner,
                owner.Operation,
                owner.InstanceId,
                lease.AcquiredAtUtc,
                status,
                entries.LastOrDefault(entry => !string.IsNullOrWhiteSpace(entry.LeaseRecovery))?.LeaseRecovery);
        }
        catch
        {
            return new GoalEvidenceLeaseFact(
                goal.Id.Value,
                lease.Owner,
                owner.Operation,
                owner.InstanceId,
                lease.AcquiredAtUtc,
                GoalEvidenceLeaseRecoveryStatus.StateUnavailable);
        }
    }

    private static GoalEvidenceLeaseRecoveryStatus ClassifyBeginOnlyOwner(
        GoalEvidenceOperationOwner owner,
        GoalOperationJournalEntry begin,
        IConductLockPidProbe? pidProbe)
    {
        if (begin.OwnerProcessStartedAtUtc is null || pidProbe is null)
        {
            return GoalEvidenceLeaseRecoveryStatus.StateUnavailable;
        }

        var ownerIsRunning = pidProbe.IsRunning(owner.ProcessId);
        var ownerIsSameProcess = ownerIsRunning &&
            pidProbe.IsSameProcess(owner.ProcessId, begin.OwnerProcessStartedAtUtc.Value);
        return ownerIsSameProcess
            ? GoalEvidenceLeaseRecoveryStatus.Live
            : GoalEvidenceLeaseRecoveryStatus.TerminalReclaimPending;
    }

    private static DateTimeOffset CurrentProcessStartedAtUtc()
    {
        using var process = Process.GetCurrentProcess();
        return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
    }

    internal static GoalEvidenceLeaseFact StateUnavailable(string goalId) =>
        new(goalId, null, null, null, null, GoalEvidenceLeaseRecoveryStatus.StateUnavailable);

    internal static void RecordRecovery(
        string executionDirectory,
        GoalId goalId,
        GoalEvidenceOperationOwner owner,
        string serializedOwner,
        DateTimeOffset at,
        string recovery)
    {
        var path = RecoveryPathFor(executionDirectory, goalId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        SharedJsonlFile.AppendLine(
            path,
            JsonSerializer.Serialize(
                new OperationRecoveryReceipt(
                    goalId.Value,
                    serializedOwner,
                    owner.Operation,
                    owner.InstanceId,
                    at,
                    recovery),
                RecoveryJsonOptions));
    }

    private static string? ReadRecovery(
        string executionDirectory,
        GoalId goalId,
        GoalEvidenceOperationOwner owner,
        string serializedOwner)
    {
        var path = RecoveryPathFor(executionDirectory, goalId);
        string? matchingRecovery = null;
        foreach (var line in SharedJsonlFile.ReadAllLines(path))
        {
            OperationRecoveryReceipt? receipt;
            try
            {
                receipt = JsonSerializer.Deserialize<OperationRecoveryReceipt>(line, RecoveryJsonOptions);
            }
            catch (JsonException ex)
            {
                throw new FormatException($"Malformed goal-evidence recovery receipt in '{path}'.", ex);
            }

            if (receipt is null)
            {
                throw new FormatException($"Malformed goal-evidence recovery receipt in '{path}'.");
            }

            if (string.Equals(receipt.GoalId, goalId.Value, StringComparison.Ordinal) &&
                string.Equals(receipt.Owner, serializedOwner, StringComparison.Ordinal) &&
                string.Equals(receipt.Operation, owner.Operation, StringComparison.Ordinal) &&
                string.Equals(receipt.OperationInstanceId, owner.InstanceId, StringComparison.Ordinal))
            {
                if (receipt.Recovery is not BeginFailedLeaseRecovery and not TerminalFailedLeaseRecovery ||
                    matchingRecovery is not null)
                {
                    throw new FormatException($"Ambiguous goal-evidence recovery receipt in '{path}'.");
                }

                matchingRecovery = receipt.Recovery;
            }
        }

        return matchingRecovery;
    }

    private static string RecoveryPathFor(string executionDirectory, GoalId goalId) =>
        Path.Combine(
            executionDirectory,
            ".orchestrator",
            "goal-evidence-recovery",
            $"{goalId.Value}.jsonl");

    private sealed record OperationRecoveryReceipt(
        string GoalId,
        string Owner,
        string Operation,
        string OperationInstanceId,
        DateTimeOffset At,
        string Recovery);
}

internal sealed class GoalEvidenceOperationScope : IDisposable
{
    private readonly IReconcileSweepRemediationStore _store;
    private readonly string _executionDirectory;
    private readonly Goal _goal;
    private readonly GoalEvidenceOperationOwner _operationOwner;
    private readonly string _serializedOwner;
    private readonly string? _leaseRecovery;
    private readonly Func<DateTimeOffset> _utcNow;
    private int _terminalized;
    private int _disposed;

    internal GoalEvidenceOperationScope(
        IReconcileSweepRemediationStore store,
        string executionDirectory,
        Goal goal,
        GoalEvidenceOperationOwner operationOwner,
        string serializedOwner,
        string? leaseRecovery,
        Func<DateTimeOffset> utcNow)
    {
        _store = store;
        _executionDirectory = executionDirectory;
        _goal = goal;
        _operationOwner = operationOwner;
        _serializedOwner = serializedOwner;
        _leaseRecovery = leaseRecovery;
        _utcNow = utcNow;
    }

    public void Complete(string? detail = null) => Terminalize(GoalOperationStatus.Completed, detail);
    public void Fail(string? detail = null) => Terminalize(GoalOperationStatus.Failed, detail);
    public void Abort(string? detail = null) => Terminalize(GoalOperationStatus.Aborted, detail);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Exception? terminalFailure = null;
        try
        {
            if (Volatile.Read(ref _terminalized) == 0)
            {
                Abort("Goal-evidence operation left scope before recording a verdict.");
            }
        }
        catch (Exception ex)
        {
            terminalFailure = ex;
        }

        Exception? recoveryFailure = null;
        if (terminalFailure is not null)
        {
            try
            {
                GoalEvidenceOperationCoordinator.RecordRecovery(
                    _executionDirectory,
                    _goal.Id,
                    _operationOwner,
                    _serializedOwner,
                    _utcNow(),
                    GoalEvidenceOperationCoordinator.TerminalFailedLeaseRecovery);
            }
            catch (Exception ex)
            {
                recoveryFailure = ex;
            }
        }

        Exception? releaseFailure = null;
        try
        {
            _store.ReleaseAcceptanceLease(_goal.Id.Value, _serializedOwner);
        }
        catch (Exception ex)
        {
            releaseFailure = ex;
        }

        if (terminalFailure is not null)
        {
            if (recoveryFailure is not null && releaseFailure is not null)
            {
                throw new AggregateException(
                    "Goal-evidence terminalization, owner-qualified release, and durable recovery recording all failed.",
                    terminalFailure,
                    releaseFailure,
                    recoveryFailure);
            }

            ExceptionDispatchInfo.Capture(terminalFailure).Throw();
        }
    }

    private void Terminalize(GoalOperationStatus status, string? detail)
    {
        if (Interlocked.CompareExchange(ref _terminalized, 1, 0) != 0)
        {
            return;
        }

        try
        {
            switch (status)
            {
                case GoalOperationStatus.Completed:
                    GoalOperationJournal.Completed(
                        _executionDirectory,
                        _goal,
                        _operationOwner.Operation,
                        detail,
                        operationInstanceId: _operationOwner.InstanceId,
                        leaseRecovery: _leaseRecovery);
                    break;
                case GoalOperationStatus.Failed:
                    GoalOperationJournal.Failed(
                        _executionDirectory,
                        _goal,
                        _operationOwner.Operation,
                        detail,
                        operationInstanceId: _operationOwner.InstanceId,
                        leaseRecovery: _leaseRecovery);
                    break;
                case GoalOperationStatus.Aborted:
                    GoalOperationJournal.Aborted(
                        _executionDirectory,
                        _goal,
                        _operationOwner.Operation,
                        detail,
                        operationInstanceId: _operationOwner.InstanceId,
                        leaseRecovery: _leaseRecovery);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported goal-evidence terminal status '{status}'.");
            }
        }
        catch
        {
            Volatile.Write(ref _terminalized, 0);
            throw;
        }
    }
}
