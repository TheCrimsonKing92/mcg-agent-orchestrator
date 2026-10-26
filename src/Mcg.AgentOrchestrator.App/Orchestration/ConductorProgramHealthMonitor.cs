using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ConductorProgramHealthMonitor(string statePath, Func<string?> policyReader,
    ConductEventLogWriter writer, TimeProvider clock,
    Func<GoalId, IReadOnlyList<ProgramHealthReceiptObservation>> receiptReader,
    ICollaborationItemStore? attention = null, ConductorInvariantObservationReader? invariantReader = null)
{
    private ICollaborationItemStore? _attention = attention;
    internal const string EventKind = "program-health";
    internal const string StateFileName = "program-health-state.json";
    internal const string AlertEvent = "PROGRAM_HEALTH_ALERT";
    internal const string ClearedEvent = "PROGRAM_HEALTH_CLEARED";

    internal static ConductorProgramHealthMonitor ForWorkspace(OrchestratorWorkspace workspace, ConductEventLogWriter writer)
        => ForWorkspace(workspace, writer, DotnetBuildEnvironmentManager.CaptureStorageRoot());

    internal static ConductorProgramHealthMonitor ForWorkspace(OrchestratorWorkspace workspace, ConductEventLogWriter writer,
        DotnetBuildStorageRoot storageRoot)
    {
        // Store setup stays inside Evaluate's advisory exception boundary, including retries after a failed open.
        MergeTrainAcceptanceStore? store = null;
        string? ReadPolicy()
        {
            try { return File.ReadAllText(Path.Combine(workspace.OrchestratorDirectory, "conductor-policy.json")); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }
        return new(Path.Combine(workspace.OrchestratorDirectory, StateFileName), ReadPolicy, writer, TimeProvider.System,
            id => (store ??= new MergeTrainAcceptanceStore(Path.Combine(workspace.OrchestratorDirectory, "merge-train-acceptance.db")))
                .ReadPassedReceiptsForGoal(id).Select(ProgramHealthReceiptObservation.FromMergeTrainReceipt).ToArray(),
            invariantReader: ConductorInvariantObservationReader.ForWorkspace(workspace, storageRoot));
    }

    internal ConductorInvariantObservationReader? InvariantReader { get; } = invariantReader;

    internal void EvaluateWithInvariants(IReadOnlyList<(GoalId Id, GoalStatus Status)> goals, string? integrationHead)
    {
        if (InvariantReader is { } reader) Evaluate(goals, integrationHead, reader.Read);
        else Evaluate(goals, integrationHead);
    }

    internal void Evaluate(IReadOnlyList<(GoalId Id, GoalStatus Status)> goals, string? integrationHead,
        Func<IReadOnlyList<(GoalId Id, GoalStatus Status)>, DateTimeOffset, ConductorInvariantObservation?>? observe = null)
    {
        if (integrationHead is null && observe is null) return;
        try
        {
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(statePath).ToUpperInvariant())));
            using var mutex = new Mutex(false, "McgProgramHealth-" + key);
            try { mutex.WaitOne(); } catch (AbandonedMutexException) { }
            try
            {
                if (integrationHead is not null)
                    try { EvaluateUnderLock(goals, integrationHead); } catch { /* Advisory, independently of invariants. */ }
                if (observe is not null)
                    try { EvaluateInvariantsUnderLock(goals, integrationHead, observe); } catch { /* Advisory, including observation reads. */ }
            }
            finally { mutex.ReleaseMutex(); }
        }
        catch
        {
            // Program health is advisory and must never interrupt conductor work.
        }
    }

    private void EvaluateUnderLock(IReadOnlyList<(GoalId Id, GoalStatus Status)> goals, string integrationHead)
    {
        var now = clock.GetUtcNow();
        var state = Load();
        var changed = state is null;
        state ??= new JsonObject
        {
            ["lastHead"] = integrationHead, ["lastLandingAt"] = null,
            ["observedSince"] = now, ["activeKey"] = null
        };
        if (changed && ReadStateObject()?["invariants"] is { } invariants)
            state["invariants"] = invariants.DeepClone();
        if (!string.Equals(state["lastHead"]!.GetValue<string>(), integrationHead, StringComparison.OrdinalIgnoreCase))
        {
            state["lastHead"] = integrationHead;
            state["lastLandingAt"] = now;
            changed = true;
        }
        var receipts = goals.Where(goal => goal.Status is GoalStatus.Verifying or GoalStatus.Verified or
            GoalStatus.WaitingForHuman or GoalStatus.Parked or GoalStatus.AcceptanceFailed)
            .SelectMany(goal => receiptReader(goal.Id)).ToArray();
        var snapshot = new ProgramHealthSnapshot(now, state["lastLandingAt"]?.GetValue<DateTimeOffset>(),
            state["observedSince"]!.GetValue<DateTimeOffset>(), integrationHead,
            goals.Count(goal => goal.Status == GoalStatus.Verified), receipts);
        var assessment = ProgramHealthEvaluator.Evaluate(snapshot, ProgramHealthThresholds.Read(policyReader()));
        var activeKey = state["activeKey"]?.GetValue<string>();
        string? detail = null;
        if (assessment.Tripped && assessment.Key != activeKey)
            detail = ProgramHealthEvaluator.FormatAlert(snapshot, assessment);
        else if (!assessment.Tripped && activeKey is not null)
            detail = ProgramHealthEvaluator.FormatCleared(activeKey, assessment);
        if (detail is not null)
        {
            if (!writer.AppendRequired(EventKind, null, detail, now, eventId: "program-health:" + Guid.NewGuid().ToString("N"))) return;
            state["activeKey"] = assessment.Tripped ? assessment.Key : null;
            changed = true;
        }
        if (changed) Save(state);
    }

    private void EvaluateInvariantsUnderLock(IReadOnlyList<(GoalId Id, GoalStatus Status)> goals, string? integrationHead,
        Func<IReadOnlyList<(GoalId Id, GoalStatus Status)>, DateTimeOffset, ConductorInvariantObservation?> observe)
    {
        var observation = observe(goals, clock.GetUtcNow());
        if (observation is null) return;
        observation = observation with { MainSha = integrationHead };
        var state = ReadStateObject() ?? new JsonObject();
        var active = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        if (state["invariants"] is JsonObject previous)
            foreach (var pair in previous)
                if (pair.Key.IndexOf('|') is > 0 && pair.Value is JsonValue value &&
                    value.TryGetValue<string>(out var text) && DateTimeOffset.TryParse(text, out var since))
                    active[pair.Key] = since;
        var assessment = ConductorInvariantWatchdog.Evaluate(observation, active);
        foreach (var breach in assessment.Raise)
        {
            var detail = $"INVARIANT_BREACH kind={breach.Kind} record={breach.Record} since={breach.Since:O} evidence={breach.Evidence}";
            if (!writer.AppendRequired("invariant-breach", breach.Record, detail, observation.Now,
                eventId: $"{breach.CorrelationKey}:{breach.Since:O}")) continue;
            Attention().RaiseAsync(CollaborationItemType.Decision,
                breach.Kind == ConductorInvariantThresholds.GateIdleWhileReadyKind ? breach.Record : null,
                $"Invariant breach: {breach.Kind} {breach.Record}",
                detail + ". Detect-only: no automatic remedy was taken.", breach.CorrelationKey).GetAwaiter().GetResult();
            active[breach.Key] = breach.Since;
            SaveActive();
        }
        foreach (var breach in assessment.Clear)
        {
            var detail = $"INVARIANT_CLEARED kind={breach.Kind} record={breach.Record}";
            if (!writer.AppendRequired("invariant-breach", breach.Record, detail, observation.Now,
                eventId: $"invariant-cleared:{breach.Kind}:{breach.Record}:{breach.Since:O}")) continue;
            Attention().TryResolveAsync(breach.CorrelationKey, detail).GetAwaiter().GetResult();
            active.Remove(breach.Key);
            SaveActive();
        }

        ICollaborationItemStore Attention() => _attention ??= CollaborationItemStore.ForDirectory(
            Path.GetDirectoryName(Path.GetFullPath(statePath))!);
        void SaveActive()
        {
            var entries = new JsonObject();
            foreach (var pair in active) entries[pair.Key] = pair.Value;
            state["invariants"] = entries;
            Save(state);
        }
    }

    private JsonObject? ReadStateObject()
    {
        try { return JsonNode.Parse(File.ReadAllText(statePath)) as JsonObject; }
        catch { return null; }
    }

    private JsonObject? Load()
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(statePath)) is not JsonObject state ||
                !state.ContainsKey("lastLandingAt") || !state.ContainsKey("activeKey")) return null;
            if (string.IsNullOrEmpty(state["lastHead"]?.GetValue<string>()) || state["observedSince"] is null) return null;
            _ = state["observedSince"]!.GetValue<DateTimeOffset>();
            _ = state["lastLandingAt"]?.GetValue<DateTimeOffset>();
            _ = state["activeKey"]?.GetValue<string>();
            return state;
        }
        catch { return null; }
    }

    private void Save(JsonObject state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(statePath))!);
        var temporaryPath = statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, state.ToJsonString());
            File.Move(temporaryPath, statePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
