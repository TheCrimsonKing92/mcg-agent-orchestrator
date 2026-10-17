using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record AcceptanceHolderBinding(string AttemptId, string MetadataPath, string Candidate, string Slot)
{
    internal string LeasePath => MetadataPath + ".holder.json";
    internal string SettlementPath => MetadataPath + ".holder-settled.json";
    internal string JobName => "Local\\mcg-acceptance-" + Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes($"{Path.GetFullPath(MetadataPath).ToUpperInvariant()}:{AttemptId}:{Candidate}:{Slot}")));
    internal static AcceptanceHolderBinding For(ConductorParallelAcceptanceAttempt attempt)
    {
        var current = new AcceptanceHolderBinding(attempt.AttemptId, attempt.MetadataPath,
            attempt.HolderCandidateKey ?? attempt.CandidateKey, $"slot-{attempt.SlotIndex}");
        return attempt.HolderContractVersion == 1 && attempt.HolderCandidateKey is null
            ? RecordedLaunchBinding(current) ?? current : current;
    }

    // Durable attempts written before HolderCandidateKey existed carry only the rewritten terminal heads.
    // Their own lease file still names the launch candidate; the attempt id (a GUID), canonical metadata
    // path and slot identify it uniquely, so only the candidate is taken from the lease.
    private static AcceptanceHolderBinding? RecordedLaunchBinding(AcceptanceHolderBinding current)
    {
        try
        {
            if (!File.Exists(current.LeasePath)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(current.LeasePath));
            var binding = document.RootElement.GetProperty("Binding");
            var candidate = binding.GetProperty("Candidate").GetString();
            return binding.GetProperty("AttemptId").GetString() == current.AttemptId &&
                binding.GetProperty("MetadataPath").GetString() == current.MetadataPath &&
                binding.GetProperty("Slot").GetString() == current.Slot && !string.IsNullOrWhiteSpace(candidate)
                ? current with { Candidate = candidate } : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
            KeyNotFoundException or InvalidOperationException)
        { return null; }
    }
    internal static AcceptanceHolderBinding For(ConductorGroupedGateAttempt attempt) =>
        new(attempt.AttemptId, attempt.MetadataPath, attempt.IdentityValue, $"{attempt.Kind}:{attempt.IdentityValue}");
}

internal sealed record AcceptanceHolderTransition(string Transition, SpawnProcessIdentity Generation,
    DateTimeOffset At, double DurationMilliseconds, string Reason);
internal sealed record AcceptanceHolderLeaseRecord(AcceptanceHolderBinding Binding, string JobName,
    SpawnProcessIdentity Generation, SpawnProcessIdentity? Holder, string Phase,
    IReadOnlyList<AcceptanceHolderTransition> Receipts);
internal sealed record AcceptanceHolderObservation(bool ReservesCapacity, bool CanAdopt, string Reason)
{
    internal bool LeaseAbsent { get; init; }
}

// Ownership writes have their own lock and artifact. They never contend with result publication.
internal sealed class AcceptanceHolderLease
{
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<SpawnProcessIdentity, SpawnTrackedProcessStatus> _classify;
    private readonly Func<string, SpawnProcessIdentity, SpawnTrackedProcessStatus, bool, AcceptanceJobObservation>? _job;
    private readonly Func<string, IDisposable>? _attach;
    private readonly Action<string>? _event;
    private readonly Func<SpawnProcessIdentity, bool> _stopUnclaimed;
    internal AcceptanceHolderLease(Func<DateTimeOffset>? utcNow = null,
        Func<SpawnProcessIdentity, SpawnTrackedProcessStatus>? classify = null,
        Func<string, SpawnProcessIdentity, SpawnTrackedProcessStatus, bool, AcceptanceJobObservation>? job = null,
        Func<string, IDisposable>? attach = null, Action<string>? eventSink = null,
        Func<SpawnProcessIdentity, bool>? stopUnclaimed = null)
    {
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _classify = classify ?? (identity => AcceptanceHolderIdentity.Classify(
            identity.ProcessId, identity.StartedAt, identity.ImagePath));
        _job = job;
        _attach = attach;
        _event = eventSink;
        _stopUnclaimed = stopUnclaimed ?? AcceptanceHolderIdentity.StopBeforeClaim;
    }

    internal static SpawnProcessIdentity CurrentGeneration() =>
        AcceptanceHolderIdentity.Capture(Environment.ProcessId) ??
        throw new InvalidOperationException("Acceptance conductor generation identity is unavailable.");

    internal void Reserve(AcceptanceHolderBinding binding, SpawnProcessIdentity generation)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(binding.LeasePath)!);
        using var gate = Lock(binding);
        if (File.Exists(binding.LeasePath)) { _ = Read(binding); return; }
        Save(new(binding, binding.JobName, generation, null, "Reserved", []));
    }

    internal IDisposable Claim(AcceptanceHolderBinding binding, SpawnProcessIdentity holder)
    {
        using var gate = Lock(binding);
        var record = Read(binding);
        if (record.Phase != "Reserved" || record.Holder is not null)
            throw new InvalidOperationException($"Acceptance holder claim refused: {record.Phase}.");
        var started = _utcNow();
        var containment = _attach?.Invoke(record.JobName) ??
            DurableAcceptanceJob.AttachCurrent(record.JobName, binding.SettlementPath);
        try
        {
            var observation = ObserveJob(record, holder, _classify(holder), false);
            if (!observation.HolderMatches || observation.Settled)
                throw new InvalidOperationException($"Acceptance containment claim failed: {observation.Detail}.");
            Save(Transition(record with { Holder = holder }, "claim", record.Generation,
                "Claimed", "job-custody-ready", started));
            return containment;
        }
        catch { containment.Dispose(); throw; }
    }

    internal AcceptanceHolderObservation Observe(AcceptanceHolderBinding binding)
    {
        try
        {
            using var gate = Lock(binding);
            var record = Read(binding);
            if (record.Phase is "Released" or "Cancelled") return new(false, false, record.Phase);
            if (record.Holder is null) return new(true, false, "holder-claim-pending");
            var status = _classify(record.Holder);
            var observation = ObserveJob(record, record.Holder, status, false);
            // Settlement is published before this method ever exposes free capacity.
            if (observation.Settled && status == SpawnTrackedProcessStatus.DeadOrRecycled)
            {
                Save(Transition(record, "release", record.Generation, "Released", observation.Detail, _utcNow()));
                return new(false, false, observation.Detail);
            }
            return new(true, status == SpawnTrackedProcessStatus.LiveMatch && observation.HolderMatches,
                observation.Detail);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Lease-required callers still hold capacity; legacy callers can distinguish absence.
            return new(true, false, "holder-lease-absent") { LeaseAbsent = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return new(true, false, $"holder-lease-unavailable:{ex.GetType().Name}");
        }
    }

    internal bool Adopt(AcceptanceHolderBinding binding, SpawnProcessIdentity generation)
    {
        using var gate = Lock(binding);
        var record = Read(binding);
        if (record.Phase is "Released" or "Cancelled" || record.Holder is null) return false;
        // A recycled conductor PID is a foreign generation. An unreadable generation remains a hold.
        if (record.Generation != generation &&
            _classify(record.Generation) != SpawnTrackedProcessStatus.DeadOrRecycled) return false;
        var started = _utcNow();
        var status = _classify(record.Holder);
        var job = ObserveJob(record, record.Holder, status, false);
        // The owning generation gets no shortcut: a holder that exited is not adoptable by anyone, or
        // its finished attempt reads as running every tick until a successor generation reconciles it.
        if (status != SpawnTrackedProcessStatus.LiveMatch || !job.HolderMatches || job.Settled) return false;
        if (record.Generation == generation && record.Phase != "Detached") return true;
        Save(Transition(record, "adopt", generation, "Adopted", job.Detail, started));
        return true;
    }

    internal bool CanAdopt(AcceptanceHolderBinding binding, SpawnProcessIdentity generation)
    {
        using var gate = Lock(binding);
        var record = Read(binding);
        if (record.Phase is "Released" or "Cancelled" || record.Holder is null ||
            (record.Generation != generation &&
             _classify(record.Generation) != SpawnTrackedProcessStatus.DeadOrRecycled)) return false;
        var status = _classify(record.Holder);
        var job = ObserveJob(record, record.Holder, status, false);
        return status == SpawnTrackedProcessStatus.LiveMatch && job.HolderMatches && !job.Settled;
    }

    internal bool ReconcileUnclaimed(AcceptanceHolderBinding binding, SpawnProcessIdentity recordedHolder)
    {
        using var gate = Lock(binding);
        var record = Read(binding);
        if (record.Phase != "Reserved" || record.Holder is not null ||
            _classify(recordedHolder) != SpawnTrackedProcessStatus.DeadOrRecycled) return false;
        // No verifier can run without a claim under this same lock. Seal a dead pre-claim launch
        // before admission, so even a delayed claim can never start work after capacity is released.
        Save(Transition(record with { Holder = recordedHolder }, "release", record.Generation,
            "Released", "unclaimed-holder-dead", _utcNow()));
        return true;
    }

    // Called only by the OS launch boundary when it failed before returning a process.
    internal void CancelUnstartedLaunch(AcceptanceHolderBinding binding, SpawnProcessIdentity generation)
    {
        using var gate = Lock(binding);
        var record = Read(binding);
        if (record.Phase != "Reserved" || record.Holder is not null || record.Generation != generation)
            throw new InvalidOperationException("Cannot abandon an acceptance launch after its holder claimed execution.");
        Save(Transition(record, "cancel", generation, "Cancelled", "process-not-started", _utcNow()));
    }

    internal void ValidateRecordedHolder(AcceptanceHolderBinding binding, SpawnProcessIdentity recordedHolder)
    {
        using var gate = Lock(binding);
        var holder = Read(binding).Holder;
        if (holder is not null && !ProcessImageResolution.SameProcess(holder, recordedHolder))
            throw new InvalidDataException("Acceptance metadata holder differs from its durable claim.");
    }

    internal bool Detach(AcceptanceHolderBinding binding, SpawnProcessIdentity generation)
    {
        using var gate = Lock(binding);
        var record = Read(binding);
        if (record.Phase is "Released" or "Cancelled") return true;
        if (record.Generation != generation)
            throw new InvalidOperationException("Acceptance detach refused: conductor generation does not own lease.");
        if (record.Phase == "Detached") return true;
        if (record.Holder is null) return false;
        var started = _utcNow();
        var status = _classify(record.Holder);
        var job = ObserveJob(record, record.Holder, status, false);
        if (status == SpawnTrackedProcessStatus.DeadOrRecycled && job.Settled)
        {
            Save(Transition(record, "release", generation, "Released", job.Detail, started));
            return true;
        }
        if (status != SpawnTrackedProcessStatus.LiveMatch || !job.HolderMatches) return false;
        Save(Transition(record, "detach", generation, "Detached", job.Detail, started));
        return true;
    }

    internal bool Cancel(AcceptanceHolderBinding binding, SpawnProcessIdentity generation, string reason,
        SpawnProcessIdentity? unclaimedHolder = null)
    {
        using var gate = Lock(binding);
        var record = Read(binding);
        if (record.Phase is "Released" or "Cancelled") return true;
        if (record.Generation != generation &&
            _classify(record.Generation) != SpawnTrackedProcessStatus.DeadOrRecycled) return false;
        var started = _utcNow();
        if (record.Holder is null)
        {
            if (record.Phase != "Reserved" || unclaimedHolder is null ||
                _classify(unclaimedHolder) == SpawnTrackedProcessStatus.Unknown ||
                !_stopUnclaimed(unclaimedHolder)) return false;
            Save(Transition(record with { Holder = unclaimedHolder }, "cancel", generation,
                "Cancelled", $"{reason}:unclaimed-holder-settled", started));
            return true;
        }
        var status = _classify(record.Holder);
        if (status == SpawnTrackedProcessStatus.Unknown) return false;
        var job = ObserveJob(record, record.Holder, status, true);
        if (!job.Settled) return false;
        // A recycled PID is never terminated. Cancellation targets only the recorded job.
        Save(Transition(record, "cancel", generation, "Cancelled", $"{reason}:{job.Detail}", started));
        return true;
    }

    internal AcceptanceHolderLeaseRecord Read(AcceptanceHolderBinding binding)
    {
        var record = JsonSerializer.Deserialize<AcceptanceHolderLeaseRecord>(File.ReadAllText(binding.LeasePath))
            ?? throw new InvalidDataException("Acceptance holder lease is empty.");
        if (record.Binding != binding || record.JobName != binding.JobName ||
            record.Phase is not ("Reserved" or "Claimed" or "Detached" or "Adopted" or "Released" or "Cancelled") ||
            !ValidIdentity(record.Generation) || record.Receipts is null ||
            record.Receipts.Any(receipt => !ValidIdentity(receipt.Generation)) ||
            (record.Holder is not null && !ValidIdentity(record.Holder)) ||
            (record.Phase is "Claimed" or "Detached" or "Adopted" or "Released" && record.Holder is null) ||
            (record.Phase == "Released" && record.Receipts.LastOrDefault()?.Transition != "release") ||
            (record.Phase == "Cancelled" && (record.Receipts.LastOrDefault()?.Transition != "cancel" ||
                record.Holder is null && record.Receipts.LastOrDefault()?.Reason != "process-not-started")))
            throw new InvalidDataException("Acceptance holder lease binding or phase changed.");
        return record;
    }

    private AcceptanceJobObservation ObserveJob(AcceptanceHolderLeaseRecord record,
        SpawnProcessIdentity holder, SpawnTrackedProcessStatus status, bool cancel)
    {
        var observation = _job?.Invoke(record.JobName, holder, status, cancel) ?? DurableAcceptanceJob.Observe(
            record.JobName, holder, status, cancel, record.Binding.SettlementPath);
        // A missing job name alone never proves settlement: the job may never have existed. But this lease
        // recorded job-custody-ready, so the named kill-on-close job did exist with the holder inside it and
        // breakaway disabled. A job object disappears only after its last handle closes, and kill-on-close then
        // terminates every member. With the holder also dead, the tree has exited even though the custodian
        // died before it could publish a receipt (2026-10-16: an activation revert killed the custodian's tree,
        // and the dead attempt held width-1 admission for 50 minutes).
        return observation is { Settled: false, Detail: "job-open-error:2" } &&
            status == SpawnTrackedProcessStatus.DeadOrRecycled && record.Holder == holder &&
            record.Receipts.Any(receipt => receipt.Transition == "claim" && receipt.Reason == "job-custody-ready")
            ? new(false, true, "job-gone-after-custody")
            : observation;
    }

    private static bool ValidIdentity(SpawnProcessIdentity? identity) => identity is not null &&
        identity.ProcessId > 0 && identity.StartedAt != DateTimeOffset.MinValue &&
        !string.IsNullOrWhiteSpace(identity.ImagePath);

    private AcceptanceHolderLeaseRecord Transition(AcceptanceHolderLeaseRecord record, string transition,
        SpawnProcessIdentity generation, string phase, string reason, DateTimeOffset started)
    {
        var now = _utcNow();
        var receipt = new AcceptanceHolderTransition(transition, generation, now,
            Math.Max(0, (now - started).TotalMilliseconds), reason);
        return record with { Generation = generation, Phase = phase, Receipts = [.. record.Receipts, receipt] };
    }

    private void Save(AcceptanceHolderLeaseRecord record)
    {
        var path = record.Binding.LeasePath;
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(record));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        if (record.Receipts.LastOrDefault() is { } receipt)
            _event?.Invoke($"ACCEPTANCE_HOLDER_{receipt.Transition.ToUpperInvariant()} " +
                $"attempt={record.Binding.AttemptId} slot={record.Binding.Slot} " +
                $"holder={record.Holder?.ProcessId} holderStartedAt={record.Holder?.StartedAt:O} " +
                $"holderImage={record.Holder?.ImagePath} generation={receipt.Generation.ProcessId} " +
                $"generationStartedAt={receipt.Generation.StartedAt:O} job={record.JobName} " +
                $"durationMs={receipt.DurationMilliseconds:0.###} reason={receipt.Reason}");
    }

    private static FileStream Lock(AcceptanceHolderBinding binding) =>
        new(binding.LeasePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
}
