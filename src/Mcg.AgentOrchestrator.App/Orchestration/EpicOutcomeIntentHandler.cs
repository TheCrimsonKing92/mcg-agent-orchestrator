using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class EpicOutcomeIntentHandler(string portfolioPath)
{
    internal bool Apply(OperatorIntentRecord intent)
    {
        if (intent.GoalId != OperatorIntentScopes.Workspace) throw new InvalidOperationException("outcome-workspace-scope-required");
        if (intent.AuthenticationAssurance == OperatorIntentAdjudication.StewardAssurance)
            throw new InvalidOperationException("steward-capability-boundary");
        var attribution = new EpicOutcomeAttribution(intent.Actor, intent.ActorKind, intent.Channel,
            OperatorIntentAdjudication.ResolveAuthorizationTier(intent.AuthenticationAssurance));
        EpicOutcomeContract.RequireMutation(attribution);
        if (!File.Exists(portfolioPath)) throw new InvalidOperationException("outcome-epic-not-found");
        // Validate before a writable open; setup is explicit for older portfolios.
        PortfolioStore.OpenReadOnly(portfolioPath);
        try
        {
            switch (intent.Verb)
            {
                case OperatorIntentVerbs.EpicOutcomeDefine:
                    var definition = JsonSerializer.Deserialize<EpicOutcomeDefineOperatorIntentPayload>(intent.PayloadJson, EpicOutcomeContract.JsonOptions)
                        ?? throw new ArgumentException("outcome-payload-invalid");
                    EpicOutcomeContract.Validate(definition.Definition);
                    return new EpicPlanStore(portfolioPath).DefineOutcomeAsync(definition.EpicId, definition.Definition,
                        intent.Id, attribution).GetAwaiter().GetResult();
                case OperatorIntentVerbs.EpicOutcomeRecord:
                    var evidence = JsonSerializer.Deserialize<EpicOutcomeRecordOperatorIntentPayload>(intent.PayloadJson, EpicOutcomeContract.JsonOptions)
                        ?? throw new ArgumentException("outcome-payload-invalid");
                    EpicOutcomeContract.Validate(evidence.Evidence);
                    return new EpicPlanStore(portfolioPath).RecordOutcomeAsync(evidence.EpicId, evidence.Evidence,
                        intent.Id, attribution).GetAwaiter().GetResult();
                default: throw new ArgumentException("outcome-verb-unsupported");
            }
        }
        catch (JsonException error) { throw new ArgumentException("outcome-payload-invalid", error); }
    }
}
