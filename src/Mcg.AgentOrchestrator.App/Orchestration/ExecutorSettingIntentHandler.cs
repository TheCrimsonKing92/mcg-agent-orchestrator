using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ExecutorSettingChange(bool Changed, string Summary);

// Host settings are applied only by the workspace intent consumer, never by submission.
internal sealed class ExecutorSettingIntentHandler(string executorsPath, string conductEventsLogPath,
    RemoteExecutorSlotsFileSync? remoteSync = null)
{
    internal RemoteExecutorSlotsFileSync? RemoteSync => remoteSync;

    internal ExecutorSettingChange Apply(OperatorIntentRecord intent, DateTimeOffset now)
    {
        if (!DecisionAuthorization.Meets(OperatorIntentAdjudication.ResolveAuthorizationTier(intent.AuthenticationAssurance),
                AuthorizationTier.Mutate))
            throw new InvalidOperationException("tier-below-mutate");
        if (intent.AuthenticationAssurance == OperatorIntentAdjudication.StewardAssurance)
            throw new InvalidOperationException("steward-capability-boundary");

        ExecutorSetSlotsOperatorIntentPayload? slots = null;
        ExecutorSetEnabledOperatorIntentPayload? enabled = null;
        FocusedEvidenceSetOperatorIntentPayload? focused = null;
        try
        {
            if (intent.Verb == OperatorIntentVerbs.ExecutorSetSlots)
            {
                slots = JsonSerializer.Deserialize<ExecutorSetSlotsOperatorIntentPayload>(intent.PayloadJson, OperatorIntentJson.Options);
                if (slots is null || string.IsNullOrWhiteSpace(slots.ExecutorId)) throw Failure("invalid-payload");
            }
            else if (intent.Verb == OperatorIntentVerbs.ExecutorSetEnabled)
            {
                enabled = JsonSerializer.Deserialize<ExecutorSetEnabledOperatorIntentPayload>(intent.PayloadJson, OperatorIntentJson.Options);
                if (enabled is null || string.IsNullOrWhiteSpace(enabled.ExecutorId) || enabled.Enabled is null)
                    throw Failure("invalid-payload");
            }
            else if (intent.Verb == OperatorIntentVerbs.FocusedEvidenceSet)
                focused = JsonSerializer.Deserialize<FocusedEvidenceSetOperatorIntentPayload>(intent.PayloadJson, OperatorIntentJson.Options)
                    ?? throw Failure("invalid-payload");
            else throw Failure("invalid-payload");
        }
        catch (JsonException error) { throw new InvalidOperationException("invalid-payload", error); }
        if (slots is not null && slots.Slots is < RemoteLaneExecutorConfiguration.MinimumSlots or > RemoteLaneExecutorConfiguration.MaximumSlots)
            throw Failure("slots-out-of-range");
        if (focused?.SampleEvery is < RemoteLaneExecutorConfiguration.MinimumSampleEvery)
            throw Failure("sample-every-out-of-range");
        if (focused?.Mode is not null and not ("off" or "shadow" or "remote-first")) throw Failure("mode-unsupported");
        if (focused is { Mode: null, SampleEvery: null }) throw Failure("no-setting");
        if (!File.Exists(executorsPath)) throw Failure("executors-file-missing");

        try
        {
            var original = File.ReadAllBytes(executorsPath);
            var before = RemoteLaneExecutorConfiguration.LoadForFocusedEvidence(executorsPath);
            RequireValid(before);

            var fields = new List<(string Field, string Old, string New)>();
            var candidate = original;
            if (enabled is not null)
            {
                var admissionEntry = before.Executors.SingleOrDefault(e => e.Id == enabled.ExecutorId)
                    ?? throw Failure("executor-not-found");
                candidate = RemoteLaneExecutorSettingRewrite.RewriteEnabled(candidate, enabled.ExecutorId, enabled.Enabled!.Value);
                fields.Add(($"executors[{enabled.ExecutorId}].enabled", Boolean(admissionEntry.AdmissionEnabled), Boolean(enabled.Enabled.Value)));
            }
            if (slots is not null)
            {
                candidate = RemoteLaneExecutorSettingRewrite.RewriteSlots(candidate, slots.ExecutorId, slots.Slots)
                    ?? throw Failure("slots-absent");
                fields.Add(($"executors[{slots.ExecutorId}].slots", Number(before.Executors.Single(e => e.Id == slots.ExecutorId).Slots), Number(slots.Slots)));
            }
            if (focused?.Mode is { } mode)
            {
                candidate = FocusedEvidenceModeRewrite.Apply(candidate, mode)
                    ?? throw Failure("focused-evidence-mode-absent");
                fields.Add(("focusedEvidence.mode", before.FocusedEvidence.Mode, mode));
            }
            if (focused?.SampleEvery is { } sample)
            {
                candidate = RemoteLaneExecutorSettingRewrite.RewriteSampleEvery(candidate, sample)
                    ?? throw Failure("sample-every-absent");
                fields.Add(("focusedEvidence.sampleEvery", Number(before.FocusedEvidence.SampleEvery), Number(sample)));
            }

            var changed = !candidate.AsSpan().SequenceEqual(original);
            var stamp = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + "." + intent.Id[..8];
            RemoteSlotsSyncResult? sync = null;
            var entry = slots is null ? null : before.Executors.Single(e => e.Id == slots.ExecutorId);
            var raise = remoteSync is not null && entry?.Transport == "ssh" && slots!.Slots > entry.Slots;
            var plannedBackup = Path.GetFileName(executorsPath) + "." + stamp + ".bak";
            if (raise)
            {
                var backupPath = Path.Combine(Path.GetDirectoryName(executorsPath)!, plannedBackup);
                if (File.Exists(backupPath) || Directory.Exists(backupPath))
                    throw new IOException("Host backup already exists.");
                sync = Sync(entry!, slots!.Slots, stamp, intent.Id, now);
            }
            var failedRaise = raise && sync?.Outcome != "synced";
            var backup = "none";
            if (changed && !failedRaise)
            {
                backup = plannedBackup;
                WriteCandidate(original, candidate, backup, before, slots, focused, enabled);
            }
            if (!raise && remoteSync is not null && entry?.Transport == "ssh")
                sync = Sync(entry, slots!.Slots, stamp, intent.Id, now);
            var remoteOutcome = slots is null || remoteSync is null ? null : sync?.Outcome ?? "not-applicable";
            var events = new ConductEventLogWriter(conductEventsLogPath, utcNow: () => now);
            foreach (var field in fields)
            {
                var detail = $"EXECUTOR_SETTING intent={intent.Id} verb={intent.Verb} field={field.Field} old={field.Old} new={field.New}" +
                    $" changed={(!failedRaise && field.Old != field.New ? "true" : "false")} backup={backup} actor={intent.Actor} actor_kind={intent.ActorKind}" +
                    (slots is not null ? SyncDetail(sync, remoteOutcome) : "");
                if (!events.AppendRequired("executor-setting", null, detail, now)) throw Failure("conduct-event-append-failed");
            }
            if (failedRaise) throw Failure("remote-slots-sync-failed");
            return new(changed, "changes=" + string.Join(",", fields.Select(f => $"{f.Field}:{f.Old}->{f.New}")) + $"; backup={backup}" +
                (remoteOutcome is null ? "" : $"; remote_slots_file={remoteOutcome}"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { throw new InvalidOperationException("executors-file-invalid", error); }
    }

    private RemoteSlotsSyncResult Sync(RemoteLaneExecutorEntry entry, int slots, string stamp, string intentId, DateTimeOffset now)
    {
        if (intentId.Length < 8 || intentId[..8].Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch != '-'))
            throw Failure("invalid-payload");
        var result = remoteSync!.Sync(entry, slots, "slots.txt." + stamp + ".bak");
        RemoteExecutorSlotsSyncLedger.Append(Path.Combine(Path.GetDirectoryName(executorsPath)!, RemoteExecutorSlotsSyncLedger.FileName),
            new(now, entry.Id, intentId, slots, result.Outcome, result.Reason, result.RemoteBackup, result.ErrorTail));
        return result;
    }

    private static string SyncDetail(RemoteSlotsSyncResult? result, string? outcome) => outcome switch
    {
        null => " remote_slots_file=unsynced",
        "synced" => $" remote_slots_file=synced remote_backup={Token(result!.RemoteBackup, "none")} remote_previous={Token(result.Previous, "absent")}",
        "failed" => $" remote_slots_file=failed remote_reason={result!.Reason}",
        _ => " remote_slots_file=not-applicable"
    };

    private static string Token(string? text, string absent) => text is null ? absent :
        new string(text.Take(64).Select(ch => char.IsWhiteSpace(ch) || char.IsControl(ch) ? '_' : ch).ToArray());

    private void WriteCandidate(byte[] original, byte[] candidate, string backup, RemoteLaneExecutorConfiguration before,
        ExecutorSetSlotsOperatorIntentPayload? slots, FocusedEvidenceSetOperatorIntentPayload? focused,
        ExecutorSetEnabledOperatorIntentPayload? enabled)
    {
        var temporary = executorsPath + "." + Guid.NewGuid().ToString("n") + ".tmp";
        try
        {
            WriteNew(temporary, candidate);
            var after = RemoteLaneExecutorConfiguration.LoadForFocusedEvidence(temporary);
            RequireValid(after);
            var expectedExecutors = before.Executors.Select(e => slots is not null && e.Id == slots.ExecutorId ? e with { Slots = slots.Slots } :
                enabled is not null && e.Id == enabled.ExecutorId ? e with { AdmissionEnabled = enabled.Enabled!.Value } : e);
            var expectedFocused = before.FocusedEvidence with
            {
                Mode = focused?.Mode ?? before.FocusedEvidence.Mode,
                SampleEvery = focused?.SampleEvery ?? before.FocusedEvidence.SampleEvery
            };
            if (!after.Executors.SequenceEqual(expectedExecutors) || !after.Lanes.SequenceEqual(before.Lanes) ||
                !after.MachineLocalResourceKeys.SequenceEqual(before.MachineLocalResourceKeys) ||
                after.AllInfrastructureLanes != before.AllInfrastructureLanes || after.FocusedEvidence != expectedFocused ||
                after.Screening.Mode != before.Screening.Mode || after.Screening.FaultReason != before.Screening.FaultReason ||
                !after.Screening.KnownDivergentTests.SequenceEqual(before.Screening.KnownDivergentTests))
                throw Failure("executors-file-invalid");
            WriteNew(Path.Combine(Path.GetDirectoryName(executorsPath)!, backup), original);
            File.Move(temporary, executorsPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void RequireValid(RemoteLaneExecutorConfiguration configuration)
    {
        if (!configuration.Enabled || configuration.FocusedEvidence.FaultReason is not null || configuration.Screening.FaultReason is not null)
            throw Failure("executors-file-invalid");
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Boolean(bool value) => value ? "true" : "false";
    private static InvalidOperationException Failure(string reason) => new(reason);
}
