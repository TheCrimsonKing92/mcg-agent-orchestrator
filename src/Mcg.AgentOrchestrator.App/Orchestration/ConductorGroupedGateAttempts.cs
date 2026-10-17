using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ConductorGroupedGateMember(
    string GoalId,
    string BranchRevision,
    string CandidateRevision,
    IReadOnlyList<string> LandingPaths,
    IReadOnlyList<string> ResourceKeys,
    ChangeRiskTier ChangeRiskTier)
{
    internal static ConductorGroupedGateMember From(GateReadyCandidateProjection member) => new(
        member.GoalId.Value, member.BranchRevision, member.CandidateRevision,
        member.LandingPaths, member.ResourceKeys, member.ChangeRiskTier);

    internal GateReadyCandidateProjection ToProjection(string mainRevision) => new(
        new GoalId(GoalId), GoalLifecycleState.Verified, GateReadyVerificationState.Satisfied,
        ChangeRiskTier, ConductorTransitionDecision.Auto, LandingPaths, ResourceKeys,
        new GateReadyMergeEvidence(BranchRevision, mainRevision,
            GateReadyMergeStatus.Clean, GateReadyMergeReason.NoConflictsDetected));
}

internal sealed record ConductorGroupedGateAttempt(
    string AttemptId,
    string Kind,
    IReadOnlyList<ConductorGroupedGateMember> Members,
    string MainRevision,
    string CombinedTreeRevision,
    string ManifestIdentity,
    string IdentityValue,
    DateTimeOffset StartedAt,
    int OwnerProcessId,
    int LaunchingGenerationId,
    string MetadataPath,
    string ResultPath,
    string ExitCodePath,
    string StdoutPath,
    string StderrPath,
    string ExecutionDirectory,
    string PolicyJson,
    DateTimeOffset? OwnerProcessStartedAt = null,
    string? OwnerExecutablePath = null,
    int? AdoptedByGenerationId = null,
    DateTimeOffset? ReconciledAt = null,
    string? Detail = null,
    string Outcome = "Running",
    string? StateDirectory = null,
    string? ProjectName = null)
{
    public int HolderContractVersion { get; init; }
}

internal sealed record ConductorGroupedGateLaunchResult(
    int ProcessId, DateTimeOffset? StartedAt, string? ExecutablePath);

internal sealed record GroupedGateAttemptOverlap(
    string FirstAttemptId, string SecondAttemptId, string FirstRecordPath, string SecondRecordPath,
    string FirstKind, string SecondKind, IReadOnlyList<string> OverlappingMembers)
{
    internal string Reason => "grouped-gate-attempt-overlap";
}

internal sealed class ConductorGroupedGateAttemptCoordinator
{
    internal const string OwnedProcessSubcommandName = "__acceptance-grouped-gate-attempt";
    private static readonly ConcurrentDictionary<int, Process> OwnedProcessDrains = new();
    private readonly string _root;
    private readonly string? _stateDirectory;
    private readonly string? _projectName;
    private readonly Func<ConductorGroupedGateAttempt, ConductorGroupedGateLaunchResult> _launch;
    private readonly Func<int, bool> _isProcessAlive;
    private readonly Func<ConductorGroupedGateAttempt, SpawnTrackedProcessStatus> _classifyHolder;
    private readonly Func<ConductorGroupedGateAttempt, bool> _stop;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<DateTimeOffset?> _hostBootTime;
    private readonly Action<string>? _event;
    private readonly DotnetBuildStorageRoot _buildStorageRoot;
    private readonly bool _usesDurableHolderLease;
    private readonly SpawnProcessIdentity _generationIdentity;
    private readonly AcceptanceHolderLease? _holderLease;
    private readonly ConcurrentDictionary<string, string> _holderHolds = new();
    private AcceptanceHolderLease HolderLease => _holderLease ?? new(utcNow: _utcNow, eventSink: _event);

    internal int GenerationId { get; }

    internal ConductorGroupedGateAttemptCoordinator(
        string root,
        int? generationId = null,
        Func<ConductorGroupedGateAttempt, ConductorGroupedGateLaunchResult>? launch = null,
        Func<int, bool>? isProcessAlive = null,
        Func<ConductorGroupedGateAttempt, bool>? stop = null,
        Func<DateTimeOffset>? utcNow = null,
        Action<string>? eventSink = null,
        DotnetBuildStorageRoot? buildStorageRoot = null,
        string? stateDirectory = null,
        string? projectName = null,
        Func<ConductorGroupedGateAttempt, SpawnTrackedProcessStatus>? classifyHolder = null,
        AcceptanceHolderLease? holderLease = null,
        SpawnProcessIdentity? generationIdentity = null,
        Func<DateTimeOffset?>? hostBootTime = null)
    {
        _root = root;
        _stateDirectory = stateDirectory;
        _projectName = projectName;
        GenerationId = generationId ?? Environment.ProcessId;
        _launch = launch ?? LaunchOwnedProcess;
        _isProcessAlive = isProcessAlive ?? IsProcessAlive;
        _classifyHolder = classifyHolder ?? (isProcessAlive is not null
            ? attempt => isProcessAlive(attempt.OwnerProcessId)
                ? SpawnTrackedProcessStatus.LiveMatch : SpawnTrackedProcessStatus.DeadOrRecycled
            : attempt => AcceptanceHolderIdentity.Classify(attempt.OwnerProcessId,
                attempt.OwnerProcessStartedAt, attempt.OwnerExecutablePath));
        _stop = stop ?? StopIdentityRevalidated;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _hostBootTime = hostBootTime ?? (() => null);
        _event = eventSink;
        _buildStorageRoot = buildStorageRoot ?? DotnetBuildEnvironmentManager.CaptureStorageRoot();
        _holderLease = holderLease;
        _usesDurableHolderLease = holderLease is not null || (launch is null && isProcessAlive is null && classifyHolder is null);
        _generationIdentity = generationIdentity ?? AcceptanceHolderLease.CurrentGeneration();
    }

    internal IEnumerable<ConductorGroupedGateAttempt> ReadAll() =>
        Directory.Exists(_root)
            ? Directory.EnumerateFiles(_root, "*.attempt.json", SearchOption.AllDirectories)
                .Select(Read)
                .OrderBy(attempt => attempt.StartedAt)
                .ToArray()
            : [];

    internal static ConductorGroupedGateAttempt Read(string path) =>
        JsonSerializer.Deserialize<ConductorGroupedGateAttempt>(File.ReadAllText(path))
        ?? throw new InvalidDataException($"Grouped gate attempt metadata is empty: {path}");

    internal static void Save(ConductorGroupedGateAttempt attempt)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(attempt.MetadataPath)!);
        var temp = attempt.MetadataPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(attempt));
            File.Move(temp, attempt.MetadataPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static T WithMetadataLock<T>(string metadataPath, Func<T> action)
    {
        var lockPath = metadataPath + ".lock";
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                using var lease = new FileStream(lockPath, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
                return action();
            }
            catch (IOException) when (attempt < 49)
            {
                Thread.Sleep(10);
            }
        }
        throw new IOException($"Could not acquire grouped gate metadata lock: {lockPath}");
    }

    private static ConductorGroupedGateAttempt Update(
        string metadataPath, Func<ConductorGroupedGateAttempt, ConductorGroupedGateAttempt> change) =>
        WithMetadataLock(metadataPath, () =>
        {
            var updated = change(Read(metadataPath));
            Save(updated);
            return updated;
        });

    internal static ConductorGroupedGateAttempt? TryClaimOwner(string metadataPath,
        int processId, DateTimeOffset startedAt, string? executablePath) =>
        WithMetadataLock(metadataPath, () =>
        {
            var current = Read(metadataPath);
            if (current.ReconciledAt is not null || current.Outcome != "Running" ||
                File.Exists(current.ResultPath) || File.Exists(current.ExitCodePath) ||
                (current.OwnerProcessId > 0 && current.OwnerProcessId != processId)) return null;
            var claimed = current with
            {
                OwnerProcessId = processId,
                OwnerProcessStartedAt = startedAt,
                OwnerExecutablePath = executablePath
            };
            Save(claimed);
            return claimed;
        });

    internal ConductorGroupedGateAttempt Create(
        string kind, IReadOnlyList<GateReadyCandidateProjection> members,
        string mainRevision, string treeRevision, string manifestIdentity,
        string identityValue, string executionDirectory, ConductorAutonomyPolicy policy)
    {
        var id = Guid.NewGuid().ToString("N");
        var prefix = Path.Combine(_root, id, id);
        return new ConductorGroupedGateAttempt(
            id, kind, members.Select(ConductorGroupedGateMember.From).ToArray(),
            mainRevision, treeRevision, manifestIdentity, identityValue, _utcNow(), 0,
            GenerationId, prefix + ".attempt.json", prefix + ".result.json", prefix + ".exit",
            prefix + ".out.log", prefix + ".err.log", executionDirectory, policy.ToJson(),
            StateDirectory: _stateDirectory, ProjectName: _projectName)
        { HolderContractVersion = _usesDurableHolderLease ? 1 : 0 };
    }

    internal ConductorGroupedGateAttempt Launch(ConductorGroupedGateAttempt attempt)
    {
        Save(attempt); // The attempt exists before a child can start.
        if (attempt.HolderContractVersion == 1)
            HolderLease.Reserve(AcceptanceHolderBinding.For(attempt), _generationIdentity);
        ConductorGroupedGateLaunchResult launched;
        try
        {
            launched = _launch(attempt);
            if (launched.ProcessId <= 0)
                throw new InvalidOperationException("Grouped gate launcher returned no process id.");
        }
        catch (Exception ex)
        {
            Update(attempt.MetadataPath, current => current with
            {
                ReconciledAt = _utcNow(), Outcome = "LaunchFailed",
                Detail = $"launch-failed:{ex.GetType().Name}:{ex.Message}"
            });
            throw;
        }
        // A child can claim its own pid before this write. If this write fails, leave the Running
        // record for that claim rather than marking a possibly live gate as a failed launch.
        try
        {
            return Update(attempt.MetadataPath, current => current.ReconciledAt is null
                ? current with
                {
                    OwnerProcessId = launched.ProcessId,
                    OwnerProcessStartedAt = launched.StartedAt ?? current.OwnerProcessStartedAt,
                    OwnerExecutablePath = launched.ExecutablePath ?? current.OwnerExecutablePath
                }
                : current);
        }
        catch (IOException)
        {
            return attempt with { OwnerProcessId = launched.ProcessId,
                OwnerProcessStartedAt = launched.StartedAt,
                OwnerExecutablePath = launched.ExecutablePath };
        }
    }

    internal bool IsAlive(int processId) => processId > 0 && _isProcessAlive(processId);

    private bool StopHolder(ConductorGroupedGateAttempt attempt, string reason) =>
        StopHolder(attempt, reason, out _);

    private bool StopHolder(ConductorGroupedGateAttempt attempt, string reason, out AcceptanceHolderLeaseFault? fault)
    {
        fault = null;
        if (attempt.HolderContractVersion != 1) return !_usesDurableHolderLease && _stop(attempt);
        var binding = AcceptanceHolderBinding.For(attempt);
        var identity = attempt.OwnerProcessStartedAt is { } started && attempt.OwnerExecutablePath is { } image
            ? new SpawnProcessIdentity(attempt.OwnerProcessId, started, image) : null;
        try
        {
            if (identity is not null) HolderLease.ValidateRecordedHolder(binding, identity);
            return HolderLease.Cancel(binding, _generationIdentity, reason, identity);
        }
        catch (Exception ex) when (AcceptanceHolderLeaseFault.From(ex, binding) is { } leaseFault)
        {
            fault = leaseFault;
            RecordHolderHold(attempt, leaseFault);
            return false;
        }
    }

    internal void PrepareHolderDetach()
    {
        if (!_usesDurableHolderLease) return;
        foreach (var attempt in ReadAll())
        {
            if (!ReservesCapacity(attempt)) continue;
            if (attempt.HolderContractVersion != 1)
            {
                RecordHolderHold(attempt, "legacy-detach-capacity-fenced");
                continue;
            }
            var binding = AcceptanceHolderBinding.For(attempt);
            try
            {
                if (attempt.OwnerProcessStartedAt is { } started && attempt.OwnerExecutablePath is { } image)
                    HolderLease.ValidateRecordedHolder(binding, new(attempt.OwnerProcessId, started, image));
                if (HolderLease.Read(binding).Generation != _generationIdentity &&
                    !HolderLease.Adopt(binding, _generationIdentity)) continue;
                if (HolderLease.Detach(binding, _generationIdentity)) continue;
                if (!StopHolder(attempt, "detach-not-provable", out var stopFault))
                {
                    if (stopFault is not null) continue;
                    throw new InvalidOperationException($"Grouped acceptance detach/cancel settlement unproven: {attempt.AttemptId}.");
                }
            }
            catch (Exception ex) when (AcceptanceHolderLeaseFault.From(ex, binding) is { } fault)
            {
                RecordHolderHold(attempt, fault);
                continue;
            }
            Reconcile(attempt, "holder-tree-cancelled-before-detach");
            File.WriteAllText(attempt.ExitCodePath, "1");
        }
    }

    // Published terminal evidence outranks leases. Legacy classification never backfills a lease.
    internal bool ReservesCapacity(ConductorGroupedGateAttempt attempt)
    {
        if (HasFinishedResult(attempt)) return false;
        var leasePresent = LeaseFilePresent(AcceptanceHolderBinding.For(attempt).LeasePath);
        if (_usesDurableHolderLease && !leasePresent &&
            (attempt.HolderContractVersion == 1 || HasCompleteHolderIdentity(attempt)))
        {
            var reserves = attempt.ReconciledAt is null && attempt.Outcome == "Running" &&
                !File.Exists(attempt.ResultPath) && !File.Exists(attempt.ExitCodePath) &&
                attempt.OwnerProcessId > 0 && _classifyHolder(attempt) != SpawnTrackedProcessStatus.DeadOrRecycled;
            if (reserves) RecordHolderHold(attempt, "legacy-pre-lease-owner-live");
            return reserves;
        }
        if (attempt.HolderContractVersion != 1 && !leasePresent)
        {
            // Injected stop boundaries attest settlement by returning success; their liveness
            // probes can still describe the old process after Refuse publishes reconciliation.
            if (!_usesDurableHolderLease) return attempt.ReconciledAt is null &&
                _classifyHolder(attempt) != SpawnTrackedProcessStatus.DeadOrRecycled;
            if (LegacyHolderSettlementReason(attempt) is null)
            {
                RecordHolderHold(attempt, "legacy-tree-settlement-unavailable");
                return true;
            }
            return WithMetadataLock(attempt.MetadataPath, () =>
            {
                var current = Read(attempt.MetadataPath);
                if (LegacyHolderSettlementReason(current) is not { } reason) return true;
                if (current.Outcome == "Running")
                {
                    if (File.Exists(current.ResultPath)) return true;
                    Save(current with { ReconciledAt = _utcNow(), Outcome = "Reconciled",
                        Detail = "legacy-holder-settled" });
                }
                var binding = AcceptanceHolderBinding.For(current);
                if (LegacyAcceptanceHolderSettlement.RecordTerminal(binding, current.OwnerProcessId,
                    current.OwnerProcessStartedAt, current.OwnerExecutablePath, current.StartedAt,
                    _utcNow(), reason))
                    _event?.Invoke($"ACCEPTANCE_HOLDER_RELEASE attempt={current.AttemptId} slot={binding.Slot} " +
                        $"holder={current.OwnerProcessId} holderStartedAt={current.OwnerProcessStartedAt:O} " +
                        $"holderImage={current.OwnerExecutablePath} receipt={LegacyAcceptanceHolderSettlement.TerminalReceiptPath(binding)} " +
                        $"reason={reason}");
                return false;
            });
        }
        var binding = AcceptanceHolderBinding.For(attempt);
        AcceptanceHolderObservation observation;
        try
        {
            if (attempt.OwnerProcessStartedAt is { } started && attempt.OwnerExecutablePath is { } image)
            {
                HolderLease.ValidateRecordedHolder(binding, new(attempt.OwnerProcessId, started, image));
                _ = HolderLease.ReconcileUnclaimed(binding, new(attempt.OwnerProcessId, started, image));
            }
            observation = HolderLease.Observe(binding);
            if (observation.CanAdopt) _ = HolderLease.Adopt(binding, _generationIdentity);
        }
        catch (Exception ex) when (AcceptanceHolderLeaseFault.From(ex, binding) is { } fault)
        {
            RecordHolderHold(attempt, fault);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            RecordHolderHold(attempt, $"holder-lease-unavailable:{ex.GetType().Name}");
            return true;
        }
        if (observation.ReservesCapacity)
        {
            if (observation.Fault is { } fault) RecordHolderHold(attempt, fault);
            else if (!observation.CanAdopt) RecordHolderHold(attempt, observation.Reason);
            return true;
        }
        if (attempt.ReconciledAt is null)
            Reconcile(attempt, $"holder-tree-settled:{observation.Reason}");
        return false;
    }

    private static bool HasCompleteHolderIdentity(ConductorGroupedGateAttempt attempt) =>
        attempt.OwnerProcessId > 0 && attempt.OwnerProcessStartedAt is not null &&
        !string.IsNullOrWhiteSpace(attempt.OwnerExecutablePath);

    private static bool LeaseFilePresent(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
    }

    internal static bool HasFinishedResult(ConductorGroupedGateAttempt attempt)
    {
        if (!File.Exists(attempt.ExitCodePath)) return false;
        try
        {
            using var stream = new FileStream(attempt.ResultPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var result = JsonDocument.Parse(stream);
            return result.RootElement.ValueKind == JsonValueKind.Object &&
                result.RootElement.TryGetProperty("Status", out var status) &&
                status.ValueKind == JsonValueKind.String && status.GetString() is "completed" or "failed";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    internal void RecordOverlap(GroupedGateAttemptOverlap overlap) =>
        _event?.Invoke($"GROUPED_GATE_ATTEMPT_OVERLAP first={overlap.FirstAttemptId} second={overlap.SecondAttemptId} " +
            $"firstRecord={overlap.FirstRecordPath} secondRecord={overlap.SecondRecordPath} " +
            $"firstKind={overlap.FirstKind} secondKind={overlap.SecondKind} " +
            $"members={string.Join('+', overlap.OverlappingMembers)} reason={overlap.Reason}");

    internal void RecordAdmissionHold(GroupedGateAttemptOverlap overlap) =>
        _event?.Invoke($"GROUPED_GATE_ADMISSION_HOLD reason={overlap.Reason} " +
            $"first={overlap.FirstRecordPath} second={overlap.SecondRecordPath}");

    private string? LegacyHolderSettlementReason(ConductorGroupedGateAttempt attempt)
    {
        if (_hostBootTime() is { } boot && LegacyAcceptanceHolderSettlement.PredatesHostBoot(attempt, boot))
            return LegacyAcceptanceHolderSettlement.PredatesHostBootEvidence;
        return LegacyAcceptanceHolderSettlement.IsAbsent(attempt.OwnerProcessId,
            attempt.OwnerProcessStartedAt, attempt.OwnerExecutablePath, () => _classifyHolder(attempt)) ||
            !(attempt.OwnerProcessId <= 0 && attempt.Outcome == "Running") &&
            LegacyAcceptanceHolderSettlement.IsSettled(AcceptanceHolderBinding.For(attempt), attempt.OwnerProcessId,
                attempt.OwnerProcessStartedAt, attempt.OwnerExecutablePath, attempt.StartedAt,
                () => _classifyHolder(attempt), attempt.ReconciledAt)
            ? "legacy-holder-absent-or-settled" : null;
    }

    private void RecordHolderHold(ConductorGroupedGateAttempt attempt, AcceptanceHolderLeaseFault fault) =>
        RecordHolderHold(attempt, fault.Format());

    private void RecordHolderHold(ConductorGroupedGateAttempt attempt, string reason)
    {
        if (_holderHolds.TryGetValue(attempt.AttemptId, out var previous) && previous == reason) return;
        _holderHolds[attempt.AttemptId] = reason;
        _event?.Invoke($"ACCEPTANCE_HOLDER_HOLD attempt={attempt.AttemptId} slot={attempt.Kind}:{attempt.IdentityValue} " +
            $"holder={attempt.OwnerProcessId} holderStartedAt={attempt.OwnerProcessStartedAt:O} " +
            $"holderImage={attempt.OwnerExecutablePath} generation={_generationIdentity.ProcessId} reason={reason}");
    }

    internal string? IdentityDifference(
        ConductorGroupedGateAttempt attempt, string kind,
        IReadOnlyList<GateReadyCandidateProjection> members,
        string mainRevision, string treeRevision, string integrationBranch)
    {
        if (!string.Equals(attempt.Kind, kind, StringComparison.Ordinal) ||
            attempt.Members.Count != members.Count ||
            !attempt.Members.Select(member => member.GoalId).ToHashSet(StringComparer.Ordinal)
                .SetEquals(members.Select(member => member.GoalId.Value)))
            return "members";
        var revisions = attempt.Members.ToDictionary(member => member.GoalId,
            member => member.CandidateRevision, StringComparer.Ordinal);
        if (members.Any(member => revisions[member.GoalId.Value] != member.CandidateRevision))
            return "candidate";
        if (!string.Equals(attempt.MainRevision, mainRevision, StringComparison.Ordinal))
            return integrationBranch;
        return !string.Equals(attempt.CombinedTreeRevision, treeRevision, StringComparison.Ordinal)
            ? "tree" : null;
    }

    internal bool ShouldAdopt(ConductorGroupedGateAttempt attempt)
    {
        // Pre-lease records are classified in place, without adoption writes.
        if (_usesDurableHolderLease && !LeaseFilePresent(AcceptanceHolderBinding.For(attempt).LeasePath)) return false;
        var binding = AcceptanceHolderBinding.For(attempt);
        try
        {
            if (attempt.HolderContractVersion == 1 && attempt.OwnerProcessStartedAt is { } started &&
                attempt.OwnerExecutablePath is { } image)
                HolderLease.ValidateRecordedHolder(binding, new(attempt.OwnerProcessId, started, image));
            return attempt.HolderContractVersion == 1
                ? HolderLease.CanAdopt(binding, _generationIdentity)
                : ConductorOrphanGateAttemptAdoption.ShouldAdopt(attempt, GenerationId, _isProcessAlive, File.Exists,
                    _classifyHolder);
        }
        catch (Exception ex) when (attempt.HolderContractVersion == 1 &&
            AcceptanceHolderLeaseFault.From(ex, binding) is { } fault)
        {
            RecordHolderHold(attempt, fault);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            RecordHolderHold(attempt, $"holder-lease-unavailable:{ex.GetType().Name}");
            return false;
        }
    }

    internal ConductorGroupedGateAttempt Adopt(ConductorGroupedGateAttempt attempt)
    {
        if (attempt.HolderContractVersion == 1)
        {
            var binding = AcceptanceHolderBinding.For(attempt);
            try
            {
                if (attempt.OwnerProcessStartedAt is { } started && attempt.OwnerExecutablePath is { } image)
                    HolderLease.ValidateRecordedHolder(binding, new(attempt.OwnerProcessId, started, image));
                if (!HolderLease.Adopt(binding, _generationIdentity)) return attempt;
            }
            catch (Exception ex) when (AcceptanceHolderLeaseFault.From(ex, binding) is { } fault)
            {
                RecordHolderHold(attempt, fault);
                return attempt;
            }
            // Grouped metadata has its own short lock; the child only claims identity here.
            // Result publication is separate, so this marker cannot overwrite a child verdict.
            return Update(attempt.MetadataPath, current => current with { AdoptedByGenerationId = GenerationId });
        }
        if (attempt.AdoptedByGenerationId == GenerationId) return attempt;
        var changed = false;
        attempt = Update(attempt.MetadataPath, current =>
        {
            if (current.AdoptedByGenerationId == GenerationId || current.ReconciledAt is not null)
                return current;
            changed = true;
            return current with { AdoptedByGenerationId = GenerationId };
        });
        if (changed) _event?.Invoke($"ACCEPTANCE_COHORT_ADOPTED kind={attempt.Kind} " +
            $"members={string.Join('+', attempt.Members.Select(member => member.GoalId))} " +
            $"identity={attempt.IdentityValue} owner={attempt.OwnerProcessId} " +
            $"launchingGeneration={attempt.LaunchingGenerationId}");
        return attempt;
    }

    internal bool Refuse(ConductorGroupedGateAttempt attempt, string field)
    {
        if (!StopHolder(attempt, $"identity-changed:{field}")) return false;
        Update(attempt.MetadataPath, current => current with
        {
            ReconciledAt = _utcNow(), Outcome = "Reconciled",
            Detail = $"refused:identity-changed field={field}"
        });
        _event?.Invoke($"ACCEPTANCE_COHORT_ADOPTION_REFUSED kind={attempt.Kind} " +
            $"members={string.Join('+', attempt.Members.Select(member => member.GoalId))} " +
            $"reason=identity-changed field={field}");
        return true;
    }

    internal bool Invalidate(ConductorGroupedGateAttempt attempt, FollowerGateInvalidReason reason)
    {
        if (attempt.Kind != "follower" || attempt.Members.Count != 2)
            throw new ArgumentException("Only follower attempts can be invalidated with a follower reason.", nameof(attempt));
        var wireReason = FollowerGateBindingRule.WireName(reason);
        var invalidated = WithMetadataLock(attempt.MetadataPath, () =>
        {
            var current = Read(attempt.MetadataPath);
            if (current.ReconciledAt is not null || current.Outcome != "Running" ||
                File.Exists(current.ResultPath) || File.Exists(current.ExitCodePath) || !StopHolder(current, wireReason)) return false;
            Save(current with
            {
                ReconciledAt = _utcNow(), Outcome = "Reconciled",
                Detail = $"follower-invalidated:{wireReason}"
            });
            return true;
        });
        if (invalidated)
            _event?.Invoke($"FOLLOWER_GATE_INVALIDATED leader={attempt.Members[0].GoalId} " +
                $"follower={attempt.Members[1].GoalId} reason={wireReason}");
        return invalidated;
    }

    internal ConductorGroupedGateAttempt Reconcile(
        ConductorGroupedGateAttempt attempt, string detail) =>
        Update(attempt.MetadataPath, current => current with
        { ReconciledAt = _utcNow(), Outcome = "Reconciled", Detail = detail });

    internal ConductorGroupedGateAttempt TryReconcileDead(
        ConductorGroupedGateAttempt attempt, string detail)
    {
        // Classification can expose free capacity without authorizing a settlement write.
        // Preserve terminal metadata and live pre-lease records, including their verdicts.
        // A published result frees capacity, but settlement still requires holder evidence below.
        if (_usesDurableHolderLease && (attempt.ReconciledAt is not null ||
            attempt.Outcome != "Running" ||
            !LeaseFilePresent(AcceptanceHolderBinding.For(attempt).LeasePath) &&
            HasCompleteHolderIdentity(attempt) && _classifyHolder(attempt) != SpawnTrackedProcessStatus.DeadOrRecycled))
            return attempt;
        var reconciled = false;
        attempt = WithMetadataLock(attempt.MetadataPath, () =>
        {
            var current = Read(attempt.MetadataPath);
            reconciled = false;
            if (current.ReconciledAt is not null || (current.HolderContractVersion == 1
                ? HolderLease.Observe(AcceptanceHolderBinding.For(current)).ReservesCapacity
                : _usesDurableHolderLease ? LegacyHolderSettlementReason(current) is null
                    : _classifyHolder(current) != SpawnTrackedProcessStatus.DeadOrRecycled)) return current;
            reconciled = true;
            var settled = current with { ReconciledAt = _utcNow(), Outcome = "Reconciled", Detail = detail };
            Save(settled);
            return settled;
        });
        if (reconciled) EmitReconciledDead(attempt);
        return attempt;
    }

    private void EmitReconciledDead(ConductorGroupedGateAttempt attempt)
    {
        if (_event is null) return;
        try
        {
            var childResult = DescribeChildResult(attempt.ResultPath);
            var eventName = childResult.Completed
                ? "ACCEPTANCE_COHORT_CHILD_COMPLETED" : "ACCEPTANCE_COHORT_RECONCILED_DEAD";
            _event($"{eventName} kind={attempt.Kind} attempt={attempt.AttemptId} " +
                $"members={string.Join('+', attempt.Members.Select(member => member.GoalId))} " +
                $"identity={attempt.IdentityValue} owner={attempt.OwnerProcessId} reason={attempt.Detail} " +
                childResult.Text + (childResult.Completed ? $" verdict={childResult.Verdict}" : "") +
                (childResult.Receipt is { } receipt ? $" receipt={receipt}" : ""));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    internal static bool IsSlotsBusyDeferral(ConductorGroupedGateAttempt attempt)
    {
        if (attempt.Kind is not ("cohort" or "train")) return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(attempt.ResultPath));
            var root = document.RootElement;
            return root.GetProperty("IdentityValue").GetString() == attempt.IdentityValue &&
                root.GetProperty("Status").GetString() == "completed" &&
                root.GetProperty("Verdict").GetString() == CohortStableSlotAcquisitionRounds.DeferredVerdict &&
                root.GetProperty("ReceiptId").GetString() == "";
        }
        catch (Exception exception) when (exception is JsonException or IOException or
            UnauthorizedAccessException or InvalidOperationException or KeyNotFoundException)
        {
            return false;
        }
    }

    private static (string Text, bool Completed, string Verdict, string? Receipt) DescribeChildResult(string path)
    {
        if (!File.Exists(path)) return ("result=none", false, "unknown", null);
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (!root.TryGetProperty("Status", out var status) || status.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(status.GetString()))
                return ("result=unreadable error=missing Status", false, "unknown", null);
            var error = root.TryGetProperty("Error", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
            var text = $"result={FirstLine(status.GetString()!)}" +
                (string.IsNullOrEmpty(error) ? "" : $" error={FirstLine(error)}");
            var completed = string.Equals(status.GetString(), "completed", StringComparison.Ordinal);
            var verdict = "unknown";
            if (completed && root.TryGetProperty("Verdict", out var recordedVerdict) &&
                recordedVerdict.ValueKind == JsonValueKind.String)
            {
                var recordedValue = recordedVerdict.GetString();
                if (string.Equals(recordedValue, "passed", StringComparison.OrdinalIgnoreCase)) verdict = "passed";
                else if (string.Equals(recordedValue, "failed", StringComparison.OrdinalIgnoreCase)) verdict = "failed";
                else if (recordedValue == CohortStableSlotAcquisitionRounds.DeferredVerdict) verdict = recordedValue;
            }
            var receipt = completed && root.TryGetProperty("ReceiptId", out var recordedReceipt) &&
                recordedReceipt.ValueKind == JsonValueKind.String
                ? recordedReceipt.GetString() : null;
            return (text, completed, verdict, string.IsNullOrEmpty(receipt) ? null : FirstLine(receipt));
        }
        catch (Exception exception) when (exception is JsonException or IOException or
            UnauthorizedAccessException or InvalidOperationException)
        {
            return ($"result=unreadable error={FirstLine(exception.Message)}", false, "unknown", null);
        }
    }

    private static string FirstLine(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        return end < 0 ? text : text[..end];
    }

    private static bool IsProcessAlive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (Exception exception) when (ProcessProbeFailure.IsNotLive(exception)) { return false; }
    }

    private static bool StopIdentityRevalidated(ConductorGroupedGateAttempt attempt)
    {
        if (attempt.OwnerProcessId <= 0 || attempt.OwnerProcessStartedAt is null ||
            string.IsNullOrWhiteSpace(attempt.OwnerExecutablePath)) return false;
        RepoProcessCliCommand.ProcessSnapshot? Snapshot()
        {
            var current = ProcessCommandLines.Snapshot([attempt.OwnerProcessId]);
            return current.Records.TryGetValue(attempt.OwnerProcessId, out var process)
                ? new RepoProcessCliCommand.ProcessSnapshot(process.ProcessId, process.ParentProcessId,
                    process.Name, process.ExecutablePath, process.StartedAt,
                    process.CommandLine, process.Status)
                : null;
        }
        var recorded = Snapshot();
        if (recorded is null) return true; // The recorded child is already gone.
        var expected = recorded with
        {
            StartedAt = attempt.OwnerProcessStartedAt,
            ExecutablePath = attempt.OwnerExecutablePath
        };
        var current = Snapshot();
        if (RepoProcessCliCommand.EvaluateStopRevalidation(
                expected, current, [OwnedProcessSubcommandName, attempt.MetadataPath]) is not null)
            return false;
        return WorkerProcessJobs.TryKillOrFallback(attempt.OwnerProcessId);
    }

    private ConductorGroupedGateLaunchResult LaunchOwnedProcess(ConductorGroupedGateAttempt attempt)
    {
        Process process;
        try
        {
            var startInfo = BuildOwnedProcessStartInfo(attempt, _buildStorageRoot);
            process = ProcessTreeGuiSuppression.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start grouped acceptance gate child.");
        }
        catch
        {
            if (attempt.HolderContractVersion == 1)
                HolderLease.CancelUnstartedLaunch(AcceptanceHolderBinding.For(attempt), _generationIdentity);
            throw;
        }
        var processId = process.Id;
        var identity = SpawnProcessIdentityReader.ReadForRegistration(process).Identity;
        try
        {
            process.StandardInput.Close();
            // The child redirects to its own durable files before executing a gate. These drains cover startup output.
            process.OutputDataReceived += (_, line) => Append(attempt.StdoutPath, line.Data);
            process.ErrorDataReceived += (_, line) => Append(attempt.StderrPath, line.Data);
            process.Exited += (_, _) =>
            {
                if (OwnedProcessDrains.TryRemove(processId, out var completed)) completed.Dispose();
            };
            process.EnableRaisingEvents = true;
            OwnedProcessDrains[processId] = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // The child owns its durable streams; an unavailable startup pipe must not be classified as
            // a failed launch after the process exists.
        }
        var result = new ConductorGroupedGateLaunchResult(processId, identity?.StartedAt, identity?.ImagePath);
        try
        {
            if (process.HasExited && OwnedProcessDrains.TryRemove(processId, out var completed))
                completed.Dispose();
        }
        catch (InvalidOperationException) { }
        return result;
    }

    internal static ProcessStartInfo BuildOwnedProcessStartInfo(
        ConductorGroupedGateAttempt attempt,
        DotnetBuildStorageRoot buildStorageRoot,
        string? executable = null,
        IReadOnlyList<string>? commandLineArgs = null)
    {
        executable ??= Environment.ProcessPath ?? "dotnet";
        commandLineArgs ??= Environment.GetCommandLineArgs();
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = attempt.ExecutionDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
            commandLineArgs.Count > 0)
            startInfo.ArgumentList.Add(commandLineArgs[0]);
        startInfo.ArgumentList.Add(OwnedProcessSubcommandName);
        startInfo.ArgumentList.Add(attempt.MetadataPath);
        GoalAcceptanceVerifier.ConfigureHermeticVerificationEnvironment(startInfo.Environment, attempt.ExecutionDirectory);
        startInfo.Environment[DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable] =
            buildStorageRoot.RootPath;
        return startInfo;
    }

    private static void Append(string path, string? line)
    {
        if (line is null) return;
        try { File.AppendAllText(path, line + Environment.NewLine); }
        catch (IOException) { }
    }
}
