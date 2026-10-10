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

    private bool Execute(OperatorIntentRecord intent, bool revert)
    {
        if (!DecisionAuthorization.Meets(OperatorIntentAdjudication.ResolveAuthorizationTier(intent.AuthenticationAssurance),
                AuthorizationTier.Mutate))
            throw new InvalidOperationException("tier-below-mutate");
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
        var allowed = target.FileKind switch
        {
            ExperimentFlagFileKind.ConductorPolicy => ConductorPolicyBooleanFlags.IsAllowed(target.PropertyName),
            ExperimentFlagFileKind.RemoteLaneExecutors => RemoteLaneExecutorFlags.IsAllowed(target.PropertyName),
            _ => throw new InvalidOperationException("file-kind-unsupported")
        };
        if (!allowed)
            throw new InvalidOperationException($"property-not-allowlisted {target.PropertyName}");
        if (revert && target.PriorValue is null) throw new InvalidOperationException("flag-not-applied");

        string? json = null;
        byte[]? executorsCandidate = null;
        bool current;
        if (target.FileKind == ExperimentFlagFileKind.RemoteLaneExecutors)
        {
            if (!File.Exists(executorsPath)) throw new InvalidOperationException("executors-file-missing");
            try
            {
                var original = File.ReadAllBytes(executorsPath);
                var configuration = RemoteLaneExecutorConfiguration.LoadForFocusedEvidence(executorsPath);
                if (configuration.DisabledReason is not null || configuration.FocusedEvidence.FaultReason is not null)
                    throw new InvalidOperationException("executors-file-invalid");
                current = configuration.FocusedEvidence.Mode == "shadow";
                executorsCandidate = RemoteLaneExecutorFlags.Rewrite(original, target.PropertyName,
                    revert ? target.PriorValue!.Value : target.ValueToApply);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            { throw new InvalidOperationException("executors-file-invalid", error); }
            if (executorsCandidate is null) throw new InvalidOperationException("focused-evidence-mode-absent");
        }
        else
        {
            if (!File.Exists(policyPath)) throw new InvalidOperationException("policy-file-missing");
            try
            {
                json = File.ReadAllText(policyPath);
                var policy = ConductorAutonomyPolicy.ParseJson(json);
                current = ConductorPolicyBooleanFlags.Read(policy, target.PropertyName);
            }
            catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException)
            { throw new InvalidOperationException("policy-file-invalid", error); }
        }

        if (!revert && target.PriorValue is null)
        {
            store.RecordFlagPriorAsync(record.Id, current).GetAwaiter().GetResult();
            record = store.ResolveAsync(record.Id).GetAwaiter().GetResult()!;
            if (record.Outcome != ExperimentOutcomeState.Open) throw new InvalidOperationException("experiment-decided");
            target = record.Spec.Intervention.FlagTarget!;
            if (target.PriorValue is null) throw new InvalidOperationException("flag-prior-capture-failed");
        }
        var desired = revert ? target.PriorValue!.Value : target.ValueToApply;
        if (current == desired) return true;
        if (executorsCandidate is not null)
            WriteAtomically(executorsPath, executorsCandidate, temporary =>
            {
                var configuration = RemoteLaneExecutorConfiguration.LoadForFocusedEvidence(temporary);
                if (configuration.DisabledReason is not null || configuration.FocusedEvidence.FaultReason is not null ||
                    (configuration.FocusedEvidence.Mode == "shadow") != desired)
                    throw new InvalidOperationException("executors-file-invalid");
            });
        else
        {
            var candidate = ExperimentPolicyPropertyRewrite.Rewrite(json!, target.PropertyName, desired);
            ConductorAutonomyPolicy.ParseJson(candidate);
            WriteAtomically(policyPath, Encoding.UTF8.GetBytes(candidate));
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
