using System.Text.Json;
using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>The workspace intent seam is the sole experiment writer of conductor policy.</summary>
internal sealed class ExperimentFlagIntentHandler(string experimentStorePath, string policyPath)
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
        if (target.FileKind != ExperimentFlagFileKind.ConductorPolicy)
            throw new InvalidOperationException("file-kind-unsupported");
        if (!ConductorPolicyBooleanFlags.IsAllowed(target.PropertyName))
            throw new InvalidOperationException($"property-not-allowlisted {target.PropertyName}");
        if (revert && target.PriorValue is null) throw new InvalidOperationException("flag-not-applied");
        if (!File.Exists(policyPath)) throw new InvalidOperationException("policy-file-missing");

        string json;
        bool current;
        try
        {
            json = File.ReadAllText(policyPath);
            var policy = ConductorAutonomyPolicy.ParseJson(json);
            current = ConductorPolicyBooleanFlags.Read(policy, target.PropertyName);
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException)
        { throw new InvalidOperationException("policy-file-invalid", error); }

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
        var candidate = ExperimentPolicyPropertyRewrite.Rewrite(json, target.PropertyName, desired);
        ConductorAutonomyPolicy.ParseJson(candidate);
        WriteAtomically(candidate);
        return false;
    }

    private void WriteAtomically(string json)
    {
        var temporary = policyPath + "." + Guid.NewGuid().ToString("n") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(Encoding.UTF8.GetBytes(json));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, policyPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
