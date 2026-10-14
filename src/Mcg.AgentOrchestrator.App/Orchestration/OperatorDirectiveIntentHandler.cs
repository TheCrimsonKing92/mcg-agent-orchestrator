using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class OperatorDirectiveIntentHandler(string databasePath)
{
    internal bool Apply(OperatorIntentRecord intent)
    {
        if (intent.GoalId != OperatorIntentScopes.Workspace)
            throw new InvalidOperationException("directive-workspace-scope-required");
        if (intent.AuthenticationAssurance == OperatorIntentAdjudication.StewardAssurance)
            throw new InvalidOperationException("steward-capability-boundary");
        var store = new SqliteOperatorDirectiveStore(databasePath);
        try
        {
            switch (intent.Verb)
            {
                case OperatorIntentVerbs.DirectiveRecord:
                    if (store.HasRecordSource(intent.Id)) return true;
                    var record = Payload<DirectiveRecordOperatorIntentPayload>(intent);
                    if (record.Kind is not ("intake" or "pacing" or "scope"))
                        throw new ArgumentException("directive-kind-unsupported");
                    if (string.IsNullOrWhiteSpace(record.Text))
                        throw new ArgumentException("directive-text-empty");
                    if (record.ExpiresAt is not null && record.UntilHandoff)
                        throw new ArgumentException("directive-expiry-conflict");
                    DateTimeOffset? expiry = null;
                    if (record.ExpiresAt is { } value)
                    {
                        if (!Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$",
                                RegexOptions.CultureInvariant) ||
                            !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ||
                            parsed.Offset != TimeSpan.Zero)
                            throw new ArgumentException("directive-expires-at-invalid");
                        expiry = parsed;
                    }
                    var text = record.Text.EndsWith("\r\n", StringComparison.Ordinal) ? record.Text[..^2] :
                        record.Text.EndsWith('\n') || record.Text.EndsWith('\r') ? record.Text[..^1] : record.Text;
                    return !store.TryAppendDirective(new OperatorDirective(Guid.NewGuid().ToString("N"),
                        record.Kind, text, intent.Actor, intent.ActorKind, intent.Channel, intent.CreatedAt,
                        expiry, record.UntilHandoff, null, null, null, false), intent.Id);
                case OperatorIntentVerbs.DirectiveRetire:
                    if (store.HasRetirementSource(intent.Id)) return true;
                    var retire = Payload<DirectiveRetireOperatorIntentPayload>(intent);
                    if (string.IsNullOrWhiteSpace(retire.DirectiveId) || string.IsNullOrWhiteSpace(retire.Reason))
                        throw new ArgumentException("directive-id-and-reason-required");
                    var result = store.TryAppendRetirement(retire.DirectiveId, intent.Id, retire.Reason,
                        intent.Actor, intent.ActorKind, intent.Channel, intent.CreatedAt);
                    if (result.Rejection is { } rejection) throw new ArgumentException(rejection);
                    return result.Replayed;
                case OperatorIntentVerbs.DirectiveHandoff:
                    if (store.HasHandoffSource(intent.Id)) return true;
                    var handoff = Payload<DirectiveHandoffOperatorIntentPayload>(intent);
                    return !store.TryAppendHandoff(Guid.NewGuid().ToString("N"), intent.Id, handoff.Note,
                        intent.Actor, intent.ActorKind, intent.Channel, intent.CreatedAt);
                default: throw new ArgumentException("directive-verb-unsupported");
            }
        }
        catch (JsonException error) { throw new ArgumentException("outcome-payload-invalid", error); }
    }

    private static T Payload<T>(OperatorIntentRecord intent) where T : class =>
        JsonSerializer.Deserialize<T>(intent.PayloadJson, OperatorIntentJson.Options)
        ?? throw new ArgumentException("outcome-payload-invalid");
}
