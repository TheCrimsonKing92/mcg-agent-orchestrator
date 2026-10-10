using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliCriterionNumberList
{
    internal static IReadOnlyList<int> Parse(string value)
    {
        var numbers = new List<int>();
        var seen = new HashSet<int>();
        foreach (var element in value.Split(','))
        {
            if (element.Length == 0 || element[0] == '0' ||
                !element.All(character => character is >= '0' and <= '9') ||
                !int.TryParse(element, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                throw new ArgumentException($"Invalid criterion element '{element}' at position {numbers.Count + 1} in '{value}'; criterion numbers are positive brief numbers.");
            if (!seen.Add(number))
                throw new ArgumentException($"Criterion {number} appears more than once in '{value}'.");
            numbers.Add(number);
        }
        return numbers;
    }

    internal static bool TrySubmit(IReadOnlyList<string> args, OrchestratorWorkspace workspace,
        Goal goal, CliPersistentStateRunner.OperatorIntentAttribution attribution)
    {
        if (!args[0].Equals(OperatorIntentVerbs.CriterionEvidenceMap, StringComparison.OrdinalIgnoreCase))
            return false;
        var criterionIndex = Array.FindIndex(args.ToArray(), value => value.Equals("--criterion", StringComparison.OrdinalIgnoreCase));
        var raw = criterionIndex < 0 || criterionIndex + 1 >= args.Count ? null : args[criterionIndex + 1];
        if (raw is null || !raw.Contains(','))
            return false;

        var numbers = Parse(raw);
        var values = CliCriterionEvidenceIntents.PositionalCriterionEvidenceArguments(args);
        var key = CliCriterionEvidenceIntents.ResolveFlagValue(args, "--idempotency-key");
        var requests = numbers.Select(number =>
        {
            var single = args.ToArray();
            for (var index = 1; index < single.Length; index++)
                if (single[index].Equals("--criterion", StringComparison.OrdinalIgnoreCase))
                {
                    single[index + 1] = number.ToString(CultureInfo.InvariantCulture);
                    break;
                }
            var target = CliCriterionEvidenceIntents.ResolveCriterionTarget(single, goal);
            var payload = CliCriterionEvidenceIntents.BuildCriterionEvidenceMappingPayload(values, target.Index, target.Version);
            var id = Guid.NewGuid().ToString("N");
            var intent = new OperatorIntentRecord(id, key is null ? id : $"{key}-{number}",
                OperatorIntentVerbs.CriterionEvidenceMap, goal.Id.Value, null,
                JsonSerializer.Serialize(payload, OperatorIntentJson.Options), [], attribution.Actor,
                attribution.Channel, attribution.AuthenticationAssurance, DateTimeOffset.UtcNow);
            return (Intent: intent, Target: target);
        }).ToArray();

        // Materialize and validate every request before opening the writable intents store.
        var store = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
        var persisted = requests.Select(request => store.EnqueueAsync(request.Intent).GetAwaiter().GetResult()).ToArray();
        foreach (var intent in persisted)
            Console.WriteLine($"Operator intent queued: id={intent.Id} verb={intent.Verb} goal={goal.Id.Value} " +
                $"status={intent.Status}; poll with operator-intent-status {intent.Id} (or add --wait).");
        foreach (var request in requests)
        {
            var target = request.Target;
            var text = target.Text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
            Console.WriteLine($"Target: {CriterionEvidenceObligation.DescribeCriterion(target.Version, target.Index)} {text[..Math.Min(80, text.Length)]}");
        }
        if (!ConductorLoopLease.IsActive(workspace.OrchestratorDirectory))
            Console.WriteLine(ConductorLoopLease.InactiveWarning);
        return true;
    }
}
