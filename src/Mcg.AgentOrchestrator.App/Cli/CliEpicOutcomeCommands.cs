using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliEpicOutcomeCommands
{
    internal static bool IsMutation(IReadOnlyList<string> parts) => parts.Count > 0 &&
        (parts[0].ToLowerInvariant() is OperatorIntentVerbs.EpicOutcomeDefine or OperatorIntentVerbs.EpicOutcomeRecord) &&
        !CliCommandHelp.IsCommandSpecificHelp(parts);

    internal static bool Submit(IReadOnlyList<string> parts, OrchestratorWorkspace workspace,
        CliPersistentStateRunner.OperatorIntentSubmissionSource submissionSource = CliPersistentStateRunner.OperatorIntentSubmissionSource.Cli)
    {
        CliCommandHelp.ThrowIfInvalidFlags(parts);
        var (reference, options, json) = Parse(parts, allowJson: true, allowIdempotency: true);
        var path = options.GetValueOrDefault("--text-file") ?? throw new ArgumentException("--text-file is required.");
        var text = File.ReadAllText(path);
        var epic = Resolve(workspace, reference);
        var attribution = CliPersistentStateRunner.ResolveOperatorIntentAttribution(parts, submissionSource);
        EpicOutcomeContract.RequireMutation(new(attribution.Actor, attribution.ActorKind, attribution.Channel,
            OperatorIntentAdjudication.ResolveAuthorizationTier(attribution.AuthenticationAssurance)));
        var verb = parts[0].ToLowerInvariant();
        string payload;
        EpicOutcomeEvidence? pendingEvidence = null;
        try
        {
            if (verb == OperatorIntentVerbs.EpicOutcomeDefine)
            {
                var definition = JsonSerializer.Deserialize<EpicOutcomeDefinition>(text, EpicOutcomeContract.JsonOptions)
                    ?? throw new ArgumentException("outcome-definition-invalid");
                EpicOutcomeContract.Validate(definition);
                payload = JsonSerializer.Serialize(new EpicOutcomeDefineOperatorIntentPayload(epic.Id, definition), EpicOutcomeContract.JsonOptions);
            }
            else if (verb == OperatorIntentVerbs.EpicOutcomeRecord)
            {
                var evidence = JsonSerializer.Deserialize<EpicOutcomeEvidence>(text, EpicOutcomeContract.JsonOptions)
                    ?? throw new ArgumentException("outcome-evidence-invalid");
                EpicOutcomeContract.Validate(evidence);
                evidence = evidence with { Source = evidence.Source with {
                    Locator = Path.GetFullPath(evidence.Source.Locator, workspace.RootDirectory) } };
                pendingEvidence = evidence;
                payload = JsonSerializer.Serialize(new EpicOutcomeRecordOperatorIntentPayload(epic.Id, evidence), EpicOutcomeContract.JsonOptions);
            }
            else throw new ArgumentException("outcome-verb-unsupported");
        }
        catch (JsonException error) { throw new ArgumentException("outcome-payload-invalid", error); }
        var id = Guid.NewGuid().ToString("n");
        var key = options.GetValueOrDefault("--idempotency-key") ?? id;
        var intents = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
        // An exact replay still returns its durable request if the original receipt has since disappeared.
        // Enqueue rechecks both identity and payload transactionally, including a concurrent submission.
        if (pendingEvidence is not null && intents.GetByIdempotencyKeyAsync(key).GetAwaiter().GetResult() is null)
            EpicOutcomeReceipts.Read(pendingEvidence.Source);
        var persisted = intents.EnqueueAsync(new OperatorIntentRecord(id, key,
                verb, OperatorIntentScopes.Workspace, null, payload, [], attribution.Actor, attribution.Channel,
                attribution.AuthenticationAssurance, DateTimeOffset.UtcNow, ActorKind: attribution.ActorKind)).GetAwaiter().GetResult();
        OperatorIntentSubmissionOutput.PrintWorkspace(persisted, workspace.OrchestratorDirectory, json);
        return false;
    }

    internal static bool ExecuteLegacyBar(IReadOnlyList<string> parts, OrchestratorWorkspace workspace,
        CliPersistentStateRunner.OperatorIntentSubmissionSource submissionSource = CliPersistentStateRunner.OperatorIntentSubmissionSource.Cli)
    {
        CliCommandHelp.ThrowIfInvalidFlags(parts);
        var (reference, options, _) = Parse(parts, allowJson: false, allowIdempotency: false);
        var path = options.GetValueOrDefault("--text-file") ?? throw new ArgumentException("--text-file is required.");
        var text = File.ReadAllText(path).Trim();
        var attribution = CliPersistentStateRunner.ResolveOperatorIntentAttribution(parts, submissionSource);
        var authority = new EpicOutcomeAttribution(attribution.Actor, attribution.ActorKind, attribution.Channel,
            OperatorIntentAdjudication.ResolveAuthorizationTier(attribution.AuthenticationAssurance));
        EpicOutcomeContract.RequireMutation(authority);
        if (!File.Exists(workspace.PortfolioStorePath)) throw new InvalidOperationException($"Epic '{reference}' was not found.");
        var (epic, _) = EpicPlanWritePreparation.Resolve(workspace.PortfolioStorePath, reference);
        new EpicPlanStore(workspace.PortfolioStorePath).SetBarAsync(epic.Id, text, authority).GetAwaiter().GetResult();
        Console.WriteLine($"Updated epic plan: [{epic.Id}] {epic.Title}");
        return false;
    }

    private static PortfolioEpic Resolve(OrchestratorWorkspace workspace, string reference)
    {
        if (!File.Exists(workspace.PortfolioStorePath)) throw new InvalidOperationException($"Epic '{reference}' was not found.");
        return PortfolioStore.OpenReadOnly(workspace.PortfolioStorePath).ResolveEpicAsync(reference).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException($"Epic '{reference}' was not found.");
    }

    private static (string Reference, Dictionary<string, string> Options, bool Json) Parse(
        IReadOnlyList<string> parts, bool allowJson, bool allowIdempotency)
    {
        if (parts.Count < 2 || string.IsNullOrWhiteSpace(parts[1]) || parts[1].StartsWith('-'))
            throw new ArgumentException("An epic reference is required.");
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var json = false;
        for (var index = 2; index < parts.Count; index++)
        {
            var flag = parts[index];
            if (flag == "--json" && allowJson)
            {
                if (json) throw new ArgumentException("--json must appear once.");
                json = true;
                continue;
            }
            if (flag is not ("--text-file" or "--operator-actor" or "--actor-kind" or "--idempotency-key") ||
                (flag == "--idempotency-key" && !allowIdempotency) || ++index >= parts.Count ||
                string.IsNullOrWhiteSpace(parts[index]) || parts[index].StartsWith("--", StringComparison.Ordinal) ||
                !options.TryAdd(flag, parts[index]))
                throw new ArgumentException("Expected each supported option once with a nonblank value.");
        }
        return (parts[1], options, json);
    }
}
