using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ExperimentDecideIntentHandler(string experimentStorePath)
{
    internal bool Apply(OperatorIntentRecord intent)
    {
        if (!DecisionAuthorization.Meets(OperatorIntentAdjudication.ResolveAuthorizationTier(intent.AuthenticationAssurance),
                AuthorizationTier.Mutate))
            throw new InvalidOperationException("tier-below-mutate");
        if (intent.AuthenticationAssurance == OperatorIntentAdjudication.StewardAssurance)
            throw new InvalidOperationException("steward-capability-boundary");
        ExperimentDecideOperatorIntentPayload? payload;
        try { payload = JsonSerializer.Deserialize<ExperimentDecideOperatorIntentPayload>(intent.PayloadJson, OperatorIntentJson.Options); }
        catch (JsonException error) { throw new ArgumentException("invalid-payload", error); }
        if (payload is null || string.IsNullOrWhiteSpace(payload.ExperimentId))
            throw new ArgumentException("invalid-payload");
        if (!File.Exists(experimentStorePath)) throw new InvalidOperationException("experiment-not-found");
        var store = new ExperimentStore(experimentStorePath);
        ExperimentRecord? record;
        try { record = store.ResolveAsync(payload.ExperimentId).GetAwaiter().GetResult(); }
        catch (InvalidOperationException error) { throw new InvalidOperationException("experiment-not-found", error); }
        if (record is null) throw new InvalidOperationException("experiment-not-found");
        if (record.Outcome != ExperimentOutcomeState.Open) throw new InvalidOperationException("experiment-decided");
        if (string.IsNullOrWhiteSpace(payload.Evidence)) throw new ArgumentException("evidence-required");
        if (string.IsNullOrWhiteSpace(payload.Action)) throw new ArgumentException("action-required");
        ExperimentOutcomeState outcome;
        try { outcome = ExperimentDecisionApplier.ParseOutcome(payload.Outcome); }
        catch (ArgumentException error) { throw new ArgumentException("invalid-outcome", error); }
        try { ExperimentDecisionApplier.Decide(store, record.Id, outcome, payload.Evidence, payload.Action); }
        catch (InvalidOperationException error) { throw new InvalidOperationException("experiment-decided", error); }
        return false;
    }
}
