using System.Text.Json;
using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>The workspace intent seam is the sole experiment writer of targeted host configuration.</summary>
internal sealed class ExperimentFlagIntentHandler(string experimentStorePath, string policyPath, string executorsPath)
{
    internal bool Apply(OperatorIntentRecord intent) => Execute(intent, revert: false);
    internal bool Revert(OperatorIntentRecord intent) => Execute(intent, revert: true);

    internal bool Toggle(OperatorIntentRecord intent)
    {
        if (intent.AuthenticationAssurance == OperatorIntentAdjudication.StewardAssurance &&
            (intent.ActorKind != OperatorActorKind.Agent || intent.Actor != "conductor" ||
             intent.Channel != "conductor-experiment-toggle"))
            throw new InvalidOperationException("steward-capability-boundary");
        RequireMutate(intent);
        string? experimentId;
        try
        {
            experimentId = JsonSerializer.Deserialize<ExperimentToggleFlagOperatorIntentPayload>(intent.PayloadJson,
                OperatorIntentJson.Options)?.ExperimentId;
        }
        catch (JsonException error) { throw new ArgumentException("invalid-payload", error); }
        if (string.IsNullOrWhiteSpace(experimentId)) throw new ArgumentException("invalid-payload");
        if (!File.Exists(experimentStorePath)) throw new InvalidOperationException("experiment-not-found");
        var record = new ExperimentStore(experimentStorePath).ResolveAsync(experimentId).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("experiment-not-found");
        if (record.Outcome != ExperimentOutcomeState.Open) throw new InvalidOperationException("experiment-decided");
        if (record.Spec.Baseline.Kind != ExperimentBaselineKind.AlternatingGates)
            throw new InvalidOperationException("not-alternating-gates");
        if (record.Spec.Intervention.Kind != ExperimentInterventionKind.ConfigFlag)
            throw new InvalidOperationException("not-config-flag");
        var target = record.Spec.Intervention.FlagTarget ?? throw new InvalidOperationException("flag-target-missing");
        if (target.PriorValue is null) throw new InvalidOperationException("flag-not-applied");
        RequireAllowed(target);
        bool Desired(bool current) => current == target.ValueToApply ? target.PriorValue.Value : target.ValueToApply;
        var candidate = ReadAndPrepare(target, Desired);
        return WriteCandidate(target, candidate, Desired(candidate.Current));
    }

    private bool Execute(OperatorIntentRecord intent, bool revert)
    {
        RequireMutate(intent);
        if (intent.AuthenticationAssurance == OperatorIntentAdjudication.StewardAssurance &&
            (!revert || intent.ActorKind != OperatorActorKind.Agent || intent.Actor != "conductor" ||
             intent.Channel != "conductor-experiment-revert"))
            throw new InvalidOperationException("steward-capability-boundary");
        string? experimentId;
        try
        {
            experimentId = revert
                ? JsonSerializer.Deserialize<ExperimentRevertFlagOperatorIntentPayload>(intent.PayloadJson, OperatorIntentJson.Options)?.ExperimentId
                : JsonSerializer.Deserialize<ExperimentApplyFlagOperatorIntentPayload>(intent.PayloadJson, OperatorIntentJson.Options)?.ExperimentId;
        }
        catch (JsonException error) { throw new ArgumentException("invalid-payload", error); }
        if (string.IsNullOrWhiteSpace(experimentId)) throw new ArgumentException("invalid-payload");
        if (!File.Exists(experimentStorePath)) throw new InvalidOperationException("experiment-not-found");
        var store = new ExperimentStore(experimentStorePath);
        var record = store.ResolveAsync(experimentId).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException("experiment-not-found");
        if (!revert && record.Outcome != ExperimentOutcomeState.Open)
            throw new InvalidOperationException("experiment-decided");
        if (record.Spec.Intervention.Kind != ExperimentInterventionKind.ConfigFlag)
            throw new InvalidOperationException("not-config-flag");
        var target = record.Spec.Intervention.FlagTarget ?? throw new InvalidOperationException("flag-target-missing");
        RequireAllowed(target);
        if (revert && target.PriorValue is null) throw new InvalidOperationException("flag-not-applied");
        var candidate = ReadAndPrepare(target, _ => revert ? target.PriorValue!.Value : target.ValueToApply);

        if (!revert && target.PriorValue is null)
        {
            store.RecordFlagPriorAsync(record.Id, candidate.Current).GetAwaiter().GetResult();
            record = store.ResolveAsync(record.Id).GetAwaiter().GetResult()!;
            if (record.Outcome != ExperimentOutcomeState.Open) throw new InvalidOperationException("experiment-decided");
            target = record.Spec.Intervention.FlagTarget!;
            if (target.PriorValue is null) throw new InvalidOperationException("flag-prior-capture-failed");
        }
        var desired = revert ? target.PriorValue!.Value : target.ValueToApply;
        return WriteCandidate(target, candidate, desired);
    }

    private static void RequireMutate(OperatorIntentRecord intent)
    {
        if (!DecisionAuthorization.Meets(OperatorIntentAdjudication.ResolveAuthorizationTier(intent.AuthenticationAssurance),
                AuthorizationTier.Mutate))
            throw new InvalidOperationException("tier-below-mutate");
    }

    private static void RequireAllowed(ExperimentFlagTarget target)
    {
        var allowed = target.FileKind switch
        {
            ExperimentFlagFileKind.ConductorPolicy => ConductorPolicyBooleanFlags.IsAllowed(target.PropertyName),
            ExperimentFlagFileKind.RemoteLaneExecutors => RemoteLaneExecutorFlags.IsAllowed(target.PropertyName),
            _ => throw new InvalidOperationException("file-kind-unsupported")
        };
        if (!allowed)
            throw new InvalidOperationException($"property-not-allowlisted {target.PropertyName}");
    }

    private (bool Current, string? Json, byte[]? Executors) ReadAndPrepare(ExperimentFlagTarget target, Func<bool, bool> desired)
        => target.FileKind == ExperimentFlagFileKind.RemoteLaneExecutors ? ReadExecutors(target, desired) : ReadPolicy(target);

    private (bool Current, string? Json, byte[]? Executors) ReadExecutors(ExperimentFlagTarget target, Func<bool, bool> desired)
    {
        byte[]? executorsCandidate = null;
        bool current;
        if (!File.Exists(executorsPath)) throw new InvalidOperationException("executors-file-missing");
        try
        {
            var original = File.ReadAllBytes(executorsPath);
            var configuration = RemoteLaneExecutorConfiguration.LoadForFocusedEvidence(executorsPath);
            if (configuration.DisabledReason is not null || configuration.FocusedEvidence.FaultReason is not null)
                throw new InvalidOperationException("executors-file-invalid");
            current = configuration.FocusedEvidence.Mode == "shadow";
            executorsCandidate = RemoteLaneExecutorFlags.Rewrite(original, target.PropertyName, desired(current));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { throw new InvalidOperationException("executors-file-invalid", error); }
        if (executorsCandidate is null) throw new InvalidOperationException("focused-evidence-mode-absent");
        return (current, null, executorsCandidate);
    }

    private (bool Current, string? Json, byte[]? Executors) ReadPolicy(ExperimentFlagTarget target)
    {
        string json;
        bool current;
        if (!File.Exists(policyPath)) throw new InvalidOperationException("policy-file-missing");
        try
        {
            json = File.ReadAllText(policyPath);
            var policy = ConductorAutonomyPolicy.ParseJson(json);
            current = ConductorPolicyBooleanFlags.Read(policy, target.PropertyName);
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException)
        { throw new InvalidOperationException("policy-file-invalid", error); }
        return (current, json, null);
    }

    private bool WriteCandidate(ExperimentFlagTarget target,
        (bool Current, string? Json, byte[]? Executors) candidate, bool desired)
    {
        if (candidate.Current == desired) return true;
        if (candidate.Executors is not null)
            WriteAtomically(executorsPath, candidate.Executors, temporary =>
            {
                var configuration = RemoteLaneExecutorConfiguration.LoadForFocusedEvidence(temporary);
                if (configuration.DisabledReason is not null || configuration.FocusedEvidence.FaultReason is not null ||
                    (configuration.FocusedEvidence.Mode == "shadow") != desired)
                    throw new InvalidOperationException("executors-file-invalid");
            });
        else
        {
            var json = ExperimentPolicyPropertyRewrite.Rewrite(candidate.Json!, target.PropertyName, desired);
            ConductorAutonomyPolicy.ParseJson(json);
            WriteAtomically(policyPath, Encoding.UTF8.GetBytes(json));
        }
        return false;
    }

    private static void WriteAtomically(string path, byte[] bytes, Action<string>? validate = null)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("n") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            validate?.Invoke(temporary);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
