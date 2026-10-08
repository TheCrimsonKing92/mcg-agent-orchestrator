using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCommandHandlers
{
    private static bool? TryExecuteExperimentCommand(string command, IReadOnlyList<string> parts, CliExecutionContext context)
    {
        if (command is not ("experiment-add" or "experiment-show" or "experiment-decide")) return null;
        var options = ExperimentOptions(parts, command == "experiment-add" ? 1 : 2);
        if (command == "experiment-add")
        {
            var path = RequiredExperimentOption(options, "--spec");
            ExperimentSpec spec;
            try
            {
                var json = File.ReadAllText(path);
                spec = JsonSerializer.Deserialize<ExperimentSpec>(json, ExperimentStore.JsonOptions)
                    ?? throw new ArgumentException("spec: expected an experiment object.");
                ValidateExperimentSpec(spec);
                using var document = JsonDocument.Parse(json);
                if (spec.Baseline.Kind == ExperimentBaselineKind.BeforeAfterWindow)
                {
                    // DateTimeOffset's JSON parser otherwise accepts timestamps without an offset.
                    var baseline = document.RootElement.EnumerateObject().First(p => p.Name.Equals("baseline", StringComparison.OrdinalIgnoreCase)).Value;
                    foreach (var field in new[] { "since", "until" })
                    {
                        var text = baseline.EnumerateObject().First(p => p.Name.Equals(field, StringComparison.OrdinalIgnoreCase)).Value.GetString();
                        if (text is null || !TryExperimentTimestamp(text, out _))
                            throw new ArgumentException($"baseline.{field}: timestamp must include an offset.");
                    }
                }
            }
            catch (JsonException error) { throw new ArgumentException($"{error.Path ?? "spec"}: invalid JSON in '{path}': {error.Message}", error); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { throw new ArgumentException($"spec: cannot read '{path}': {error.Message}", error); }
            if (spec.EpicId is not null)
            {
                var epic = File.Exists(context.Workspace.PortfolioStorePath)
                    ? PortfolioStore.OpenReadOnly(context.Workspace.PortfolioStorePath).ResolveEpicAsync(spec.EpicId).GetAwaiter().GetResult()
                    : null;
                if (epic is null) throw new InvalidOperationException($"epicId: epic '{spec.EpicId}' was not found.");
                spec = spec with { EpicId = epic.Id };
            }
            var added = new ExperimentStore(context.Workspace.ExperimentStorePath).AddAsync(spec).GetAwaiter().GetResult();
            Console.WriteLine($"experiment: {added.Id}");
            return false;
        }
        if (parts.Count < 2 || parts[1].StartsWith('-')) throw new ArgumentException($"{command}: experiment reference is required.");
        var store = new ExperimentStore(context.Workspace.ExperimentStorePath);
        var record = store.ResolveAsync(parts[1]).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException($"Experiment '{parts[1]}' was not found.");
        if (command == "experiment-decide")
        {
            var outcomeText = RequiredExperimentOption(options, "--outcome");
            var outcome = outcomeText switch
            {
                "confirmed" => ExperimentOutcomeState.Confirmed, "refuted" => ExperimentOutcomeState.Refuted,
                "inconclusive" => ExperimentOutcomeState.Inconclusive,
                _ => throw new ArgumentException("outcome: expected confirmed, refuted or inconclusive.")
            };
            store.DecideAsync(record.Id, outcome, RequiredExperimentOption(options, "--evidence"),
                RequiredExperimentOption(options, "--action")).GetAwaiter().GetResult();
            Console.WriteLine($"outcome: {outcomeText} (experiment {record.Id})");
            return false;
        }
        var asOf = DateTimeOffset.UtcNow;
        if (options.TryGetValue("--as-of", out var textAsOf) && !TryExperimentTimestamp(textAsOf, out asOf))
            throw new ArgumentException("as-of: timestamp must include an offset.");
        WriteExperimentRecord(record);
        IReadOnlyCollection<Goal> goals = [];
        if (File.Exists(context.Workspace.SqliteStatePath))
        {
            var queries = SqliteOrchestratorStateRepository.OpenReadOnly(context.Workspace.SqliteStatePath);
            var metadata = queries.ListGoalMetadataAsync().GetAwaiter().GetResult();
            if (metadata.Count > 0)
                goals = queries.LoadGoalsAsync(metadata.Select(g => new GoalId(g.Id)).ToArray()).GetAwaiter().GetResult().Goals;
        }
        ExperimentReading.Write(record, goals, ReadExperimentLandings(context.Workspace.GoalLifecycleEventsDirectory),
            CliOwnerDigestRetryIntents.Read(context.Workspace, asOf), asOf);
        Console.WriteLine($"outcome: {ExperimentReading.Name(record.Outcome)}");
        return false;
    }

    private static Dictionary<string, string> ExperimentOptions(IReadOnlyList<string> parts, int start)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = start; i < parts.Count; i += 2)
        {
            if (!parts[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= parts.Count || !options.TryAdd(parts[i], parts[i + 1]))
                throw new ArgumentException($"{parts[0]}: expected each option once with a value.");
        }
        return options;
    }

    private static string RequiredExperimentOption(IReadOnlyDictionary<string, string> options, string flag) =>
        options.TryGetValue(flag, out var value) && !string.IsNullOrWhiteSpace(value) ? value :
            throw new ArgumentException($"{flag[2..]}: required non-empty option.");

    private static void ValidateExperimentSpec(ExperimentSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Hypothesis)) throw new ArgumentException("hypothesis: required non-empty text.");
        if (spec.Intervention is null || !Enum.IsDefined(spec.Intervention.Kind)) throw new ArgumentException("intervention.kind: unknown or missing kind.");
        if (string.IsNullOrWhiteSpace(spec.Intervention.Description)) throw new ArgumentException("intervention.description: required non-empty text.");
        if (spec.Baseline is null || !Enum.IsDefined(spec.Baseline.Kind)) throw new ArgumentException("baseline.kind: unknown or missing kind.");
        if (spec.Baseline.Kind == ExperimentBaselineKind.BeforeAfterWindow &&
            (spec.Baseline.Since is null || spec.Baseline.Until is null || spec.Baseline.Since >= spec.Baseline.Until))
            throw new ArgumentException("baseline: before-after-window requires since < until.");
        if (spec.Baseline.Kind == ExperimentBaselineKind.TwinGoal && string.IsNullOrWhiteSpace(spec.Baseline.TwinGoalId))
            throw new ArgumentException("baseline.twinGoalId: required for twin-goal.");
        if (spec.Baseline.Kind == ExperimentBaselineKind.AlternatingGates &&
            (spec.Baseline.Since is null || spec.Baseline.Until is null || spec.Baseline.Since >= spec.Baseline.Until))
            throw new ArgumentException("baseline: alternating-gates requires a window with since < until.");
        if (spec.Metrics is null || spec.Metrics.Count is < 1 or > 3 || spec.Metrics.Distinct().Count() != spec.Metrics.Count ||
            spec.Metrics.Any(m => !ExperimentMetrics.Menu.Contains(m))) throw new ArgumentException("metrics: expected 1–3 distinct fixed-menu metrics.");
        if (spec.Guardrail is null || !ExperimentMetrics.Menu.Contains(spec.Guardrail.Metric) || spec.Metrics.Contains(spec.Guardrail.Metric))
            throw new ArgumentException("guardrail.metric: expected a fixed-menu metric separate from metrics.");
        ValidateExperimentCondition(spec.Guardrail.BreachIf, [spec.Guardrail.Metric], "guardrail.breachIf");
        if (spec.StopRule is null || !Enum.IsDefined(spec.StopRule.Unit)) throw new ArgumentException("stopRule.unit: expected gates, goals or ticks.");
        if (spec.StopRule.Count <= 0) throw new ArgumentException("stopRule.count: must be positive.");
        if (spec.DecisionRule is null) throw new ArgumentException("decisionRule: required.");
        ValidateExperimentConditions(spec.DecisionRule.KeepIf, spec.Metrics, "decisionRule.keepIf");
        ValidateExperimentConditions(spec.DecisionRule.RevertIf, spec.Metrics, "decisionRule.revertIf");
        if (spec.EpicId is not null && string.IsNullOrWhiteSpace(spec.EpicId)) throw new ArgumentException("epicId: must be non-empty when supplied.");
    }

    private static void ValidateExperimentConditions(IReadOnlyList<ExperimentCondition>? conditions, IReadOnlyList<string> metrics, string field)
    {
        if (conditions is null || conditions.Count == 0) throw new ArgumentException($"{field}: at least one condition is required.");
        foreach (var condition in conditions) ValidateExperimentCondition(condition, metrics, field);
    }

    private static void ValidateExperimentCondition(ExperimentCondition? condition, IReadOnlyList<string> metrics, string field)
    {
        if (condition is null || !metrics.Contains(condition.Metric)) throw new ArgumentException($"{field}.metric: must name its declared metric.");
        if (condition.Op is not ("<" or "<=" or ">" or ">=")) throw new ArgumentException($"{field}.op: expected <, <=, > or >=.");
        if (!double.IsFinite(condition.ChangePercent)) throw new ArgumentException($"{field}.changePercent: must be finite.");
    }

    private static bool TryExperimentTimestamp(string text, out DateTimeOffset at)
    {
        at = default;
        var hasOffset = text.EndsWith('Z') || (text.Length >= 6 && text[^6] is '+' or '-' && text[^3] == ':');
        return hasOffset && DateTimeOffset.TryParseExact(text, ["yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out at);
    }

    private static void WriteExperimentRecord(ExperimentRecord record)
    {
        var spec = record.Spec;
        Console.WriteLine($"experiment: {record.Id}");
        Console.WriteLine($"created at: {record.CreatedAt:O}");
        Console.WriteLine($"hypothesis: {spec.Hypothesis}");
        Console.WriteLine($"intervention kind: {ExperimentReading.Name(spec.Intervention.Kind)}");
        Console.WriteLine($"intervention description: {spec.Intervention.Description}");
        Console.WriteLine($"baseline kind: {ExperimentReading.Name(spec.Baseline.Kind)}");
        Console.WriteLine($"baseline since: {spec.Baseline.Since:O}");
        Console.WriteLine($"baseline until: {spec.Baseline.Until:O}");
        Console.WriteLine($"baseline twin goal: {spec.Baseline.TwinGoalId ?? "none"}");
        Console.WriteLine($"metrics: {string.Join(", ", spec.Metrics)}");
        Console.WriteLine($"guardrail: {JsonSerializer.Serialize(spec.Guardrail, ExperimentStore.JsonOptions)}");
        Console.WriteLine($"stop rule target: {spec.StopRule.Count} {ExperimentReading.Name(spec.StopRule.Unit)}");
        Console.WriteLine($"decision rule: {JsonSerializer.Serialize(spec.DecisionRule, ExperimentStore.JsonOptions)}");
        Console.WriteLine($"epic id: {spec.EpicId ?? "none"}");
        Console.WriteLine($"decision evidence: {record.Decision?.Evidence ?? "none"}");
        Console.WriteLine($"decision action: {record.Decision?.Action ?? "none"}");
        Console.WriteLine($"decided at: {record.Decision?.DecidedAt:O}");
    }

    private static IReadOnlyDictionary<string, DateTimeOffset> ReadExperimentLandings(string directory)
    {
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        if (!Directory.Exists(directory)) return result;
        foreach (var path in Directory.EnumerateFiles(directory, "*.jsonl"))
        foreach (var line in File.ReadLines(path))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("eventType", out var kind) || kind.GetString() != "GoalLanded") continue;
                var id = root.GetProperty("goalId").GetString();
                var at = root.GetProperty("timestamp").GetDateTimeOffset();
                if (id is not null && (!result.TryGetValue(id, out var previous) || at < previous)) result[id] = at;
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { }
        }
        return result;
    }
}
