using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ExecutorSettingChange(bool Changed, string Summary);

// Host settings are applied only by the workspace intent consumer, never by submission.
internal sealed class ExecutorSettingIntentHandler(string executorsPath, string conductEventsLogPath)
{
    internal ExecutorSettingChange Apply(OperatorIntentRecord intent, DateTimeOffset now)
    {
        if (!DecisionAuthorization.Meets(OperatorIntentAdjudication.ResolveAuthorizationTier(intent.AuthenticationAssurance),
                AuthorizationTier.Mutate))
            throw new InvalidOperationException("tier-below-mutate");
        if (intent.AuthenticationAssurance == OperatorIntentAdjudication.StewardAssurance)
            throw new InvalidOperationException("steward-capability-boundary");

        ExecutorSetSlotsOperatorIntentPayload? slots = null;
        FocusedEvidenceSetOperatorIntentPayload? focused = null;
        try
        {
            if (intent.Verb == OperatorIntentVerbs.ExecutorSetSlots)
            {
                slots = JsonSerializer.Deserialize<ExecutorSetSlotsOperatorIntentPayload>(intent.PayloadJson, OperatorIntentJson.Options);
                if (slots is null || string.IsNullOrWhiteSpace(slots.ExecutorId)) throw Failure("invalid-payload");
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
        if (focused?.Mode is not null and not ("off" or "shadow")) throw Failure("mode-unsupported");
        if (focused is { Mode: null, SampleEvery: null }) throw Failure("no-setting");
        if (!File.Exists(executorsPath)) throw Failure("executors-file-missing");

        try
        {
            var original = File.ReadAllBytes(executorsPath);
            var before = RemoteLaneExecutorConfiguration.LoadForFocusedEvidence(executorsPath);
            RequireValid(before);
            if (focused?.Mode is not null && before.FocusedEvidence.Mode == "remote-first")
                throw Failure("focused-evidence-mode-remote-first");

            var fields = new List<(string Field, string Old, string New)>();
            var candidate = original;
            if (slots is not null)
            {
                candidate = RemoteLaneExecutorSettingRewrite.RewriteSlots(candidate, slots.ExecutorId, slots.Slots)
                    ?? throw Failure("slots-absent");
                fields.Add(($"executors[{slots.ExecutorId}].slots", Number(before.Executors.Single(e => e.Id == slots.ExecutorId).Slots), Number(slots.Slots)));
            }
            if (focused?.Mode is { } mode)
            {
                candidate = RemoteLaneExecutorFlags.Rewrite(candidate, RemoteLaneExecutorFlags.FocusedEvidenceShadow, mode == "shadow")
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
            var backup = "none";
            if (changed)
            {
                backup = Path.GetFileName(executorsPath) + "." + now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)
                    + "." + intent.Id[..8] + ".bak";
                WriteCandidate(original, candidate, backup, before, slots, focused);
            }
            var events = new ConductEventLogWriter(conductEventsLogPath, utcNow: () => now);
            foreach (var field in fields)
            {
                var detail = $"EXECUTOR_SETTING intent={intent.Id} verb={intent.Verb} field={field.Field} old={field.Old} new={field.New}" +
                    $" changed={(field.Old != field.New ? "true" : "false")} backup={backup} actor={intent.Actor} actor_kind={intent.ActorKind}" +
                    (slots is not null ? " remote_slots_file=unsynced" : "");
                if (!events.AppendRequired("executor-setting", null, detail, now)) throw Failure("conduct-event-append-failed");
            }
            return new(changed, "changes=" + string.Join(",", fields.Select(f => $"{f.Field}:{f.Old}->{f.New}")) + $"; backup={backup}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { throw new InvalidOperationException("executors-file-invalid", error); }
    }

    private void WriteCandidate(byte[] original, byte[] candidate, string backup, RemoteLaneExecutorConfiguration before,
        ExecutorSetSlotsOperatorIntentPayload? slots, FocusedEvidenceSetOperatorIntentPayload? focused)
    {
        var temporary = executorsPath + "." + Guid.NewGuid().ToString("n") + ".tmp";
        try
        {
            WriteNew(temporary, candidate);
            var after = RemoteLaneExecutorConfiguration.LoadForFocusedEvidence(temporary);
            RequireValid(after);
            var expectedExecutors = before.Executors.Select(e => slots is not null && e.Id == slots.ExecutorId ? e with { Slots = slots.Slots } : e);
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
    private static InvalidOperationException Failure(string reason) => new(reason);
}
