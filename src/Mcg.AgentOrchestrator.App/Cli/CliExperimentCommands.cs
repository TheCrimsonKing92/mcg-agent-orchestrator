using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliExperimentCommands
{
    internal static bool IsCommand(string command) =>
        command.ToLowerInvariant() is "experiment-add" or "experiment-show" or "experiment-decide" or "experiment-extend" or "experiment-apply-flag";

    internal static bool? TryExecute(string command, IReadOnlyList<string> parts, CliExecutionContext context)
    {
        if (!IsCommand(command)) return null;
        if (command == "experiment-apply-flag") return CliExperimentFlagCommands.SubmitApply(parts, context.Workspace);
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
            var experimentStore = new ExperimentStore(context.Workspace.ExperimentStorePath);
            var prior = experimentStore.ListAllAsync().GetAwaiter().GetResult();
            var added = experimentStore.AddAsync(spec).GetAwaiter().GetResult();
            foreach (var overlap in ExperimentOverlap.Find(added, prior))
                Console.WriteLine($"overlap: {overlap.Other.Id} shared: {string.Join(", ", overlap.SharedMetrics)} hypothesis: {overlap.Other.Spec.Hypothesis.ReplaceLineEndings(" ")}");
            Console.WriteLine($"experiment: {added.Id}");
            return false;
        }
        if (parts.Count < 2 || parts[1].StartsWith('-')) throw new ArgumentException($"{command}: experiment reference is required.");
        var store = command == "experiment-show"
            ? ExperimentStore.OpenReadOnly(context.Workspace.ExperimentStorePath)
            : new ExperimentStore(context.Workspace.ExperimentStorePath);
        var record = store.ResolveAsync(parts[1]).GetAwaiter().GetResult()
            ?? throw new InvalidOperationException($"Experiment '{parts[1]}' was not found.");
        if (command == "experiment-decide")
        {
            var outcomeText = RequiredExperimentOption(options, "--outcome");
            var outcome = ExperimentDecisionApplier.ParseOutcome(outcomeText);
            var evidence = RequiredExperimentOption(options, "--evidence");
            var action = RequiredExperimentOption(options, "--action");
            ExperimentDecisionApplier.Decide(store, record.Id, outcome, evidence, action);
            Console.WriteLine($"outcome: {outcomeText} (experiment {record.Id})");
            return false;
        }
        if (command == "experiment-extend")
        {
            var countText = RequiredExperimentOption(options, "--count");
            if (!int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
                throw new ArgumentException("count: expected a whole number.");
            var reason = RequiredExperimentOption(options, "--reason");
            var updated = ExperimentExtensionApplier.Extend(store, record.Id, count, reason, DateTimeOffset.UtcNow);
            var extension = updated.Spec.StopRule.Extensions!.Single(entry => entry.NewCount == count);
            Console.WriteLine($"stop rule extended: {extension.FromCount} to {extension.NewCount} (experiment {updated.Id})");
            return false;
        }
        var asOf = DateTimeOffset.UtcNow;
        if (options.TryGetValue("--as-of", out var textAsOf) && !TryExperimentTimestamp(textAsOf, out asOf))
            throw new ArgumentException("as-of: timestamp must include an offset.");
        WriteExperimentRecord(record);
        var goals = ExperimentGoals.Read(context.Workspace.SqliteStatePath);
        var gateCount = record.Spec.StopRule.Unit == ExperimentStopUnit.Gates
            ? ExperimentGateAttempts.Count(ExperimentGateAttempts.Read(context.Workspace.ConductEventsLogPath),
                ExperimentReading.ComparisonStart(record), asOf)
            : (int?)null;
        ExperimentReading.Render(ExperimentReading.Evaluate(record, goals, ExperimentLandingTimes.Read(context.Workspace.GoalLifecycleEventsDirectory),
            CliOwnerDigestRetryIntents.Read(context.Workspace, asOf), asOf, gateCount));
        Console.WriteLine($"outcome: {ExperimentReading.Name(record.Outcome)}");
        var overlaps = ExperimentOverlap.Find(record, store.ListAllAsync().GetAwaiter().GetResult());
        if (overlaps.Count == 0) Console.WriteLine("overlaps: none");
        else
        {
            Console.WriteLine("overlaps:");
            foreach (var overlap in overlaps)
            {
                var state = overlap.Other.Decision is { } decision ? $"decided at {decision.DecidedAt:O}" : "open";
                Console.WriteLine($"  {overlap.Other.Id} {state} shared: {string.Join(", ", overlap.SharedMetrics)}");
            }
        }
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
        if (spec.Intervention.FlagTarget is { } target)
        {
            if (spec.Intervention.Kind != ExperimentInterventionKind.ConfigFlag)
                throw new ArgumentException("intervention.flagTarget: only config-flag interventions can name a flag target.");
            var allowed = target.FileKind switch
            {
                ExperimentFlagFileKind.ConductorPolicy => ConductorPolicyBooleanFlags.IsAllowed(target.PropertyName),
                ExperimentFlagFileKind.RemoteLaneExecutors => RemoteLaneExecutorFlags.IsAllowed(target.PropertyName),
                _ => throw new ArgumentException("intervention.flagTarget.fileKind: expected conductor-policy or remote-lane-executors.")
            };
            if (!allowed)
                throw new ArgumentException($"intervention.flagTarget.propertyName: property-not-allowlisted {target.PropertyName}");
            if (target.PriorValue is not null)
                throw new ArgumentException("intervention.flagTarget.priorValue: captured by apply; must not be supplied.");
        }
        if (spec.Baseline is null || !Enum.IsDefined(spec.Baseline.Kind)) throw new ArgumentException("baseline.kind: unknown or missing kind.");
        if (spec.Baseline.Kind == ExperimentBaselineKind.BeforeAfterWindow &&
            (spec.Baseline.Since is null || spec.Baseline.Until is null || spec.Baseline.Since >= spec.Baseline.Until))
            throw new ArgumentException("baseline: before-after-window requires since < until.");
        if (spec.Baseline.Kind == ExperimentBaselineKind.TwinGoal && string.IsNullOrWhiteSpace(spec.Baseline.TwinGoalId))
            throw new ArgumentException("baseline.twinGoalId: required for twin-goal.");
        if (spec.Baseline.Kind == ExperimentBaselineKind.TwinGoal &&
            (string.IsNullOrWhiteSpace(spec.Baseline.ComparisonGoalId) ||
             string.Equals(spec.Baseline.ComparisonGoalId.Trim(), spec.Baseline.TwinGoalId!.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("baseline.comparisonGoalId: required for twin-goal and must differ from twinGoalId.");
        if (spec.Baseline.Kind == ExperimentBaselineKind.GoalCohort)
        {
            var baselineIds = ValidateCohortGoalIds(spec.Baseline.BaselineGoalIds, "baseline.baselineGoalIds");
            var comparisonIds = ValidateCohortGoalIds(spec.Baseline.ComparisonGoalIds, "baseline.comparisonGoalIds");
            foreach (var id in comparisonIds)
                if (baselineIds.Contains(id))
                    throw new ArgumentException($"baseline.comparisonGoalIds: id '{id}' also appears in baselineGoalIds.");
        }
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
        if (spec.StopRule.Extensions is not null)
            throw new ArgumentException("stopRule.extensions: recorded by experiment-extend; must not be supplied.");
        if (spec.DecisionRule is null) throw new ArgumentException("decisionRule: required.");
        ValidateExperimentConditions(spec.DecisionRule.KeepIf, spec.Metrics, "decisionRule.keepIf");
        ValidateExperimentConditions(spec.DecisionRule.RevertIf, spec.Metrics, "decisionRule.revertIf");
        if (spec.EpicId is not null && string.IsNullOrWhiteSpace(spec.EpicId)) throw new ArgumentException("epicId: must be non-empty when supplied.");
    }

    private static HashSet<string> ValidateCohortGoalIds(IReadOnlyList<string>? references, string field)
    {
        if (references is null || references.Count == 0)
            throw new ArgumentException($"{field}: required non-empty list for goal-cohort.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in references)
        {
            if (string.IsNullOrWhiteSpace(reference))
                throw new ArgumentException($"{field}: entries must be non-blank.");
            var id = reference.Trim();
            if (!ids.Add(id)) throw new ArgumentException($"{field}: repeated id '{id}'.");
        }
        return ids;
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
        if (spec.Intervention.FlagTarget is { } target)
            Console.WriteLine($"intervention flag target: {JsonSerializer.Serialize(target, ExperimentStore.JsonOptions)}");
        Console.WriteLine($"baseline kind: {ExperimentReading.Name(spec.Baseline.Kind)}");
        Console.WriteLine($"baseline since: {spec.Baseline.Since:O}");
        Console.WriteLine($"baseline until: {spec.Baseline.Until:O}");
        Console.WriteLine($"baseline twin goal: {spec.Baseline.TwinGoalId ?? "none"}");
        Console.WriteLine($"baseline comparison goal: {spec.Baseline.ComparisonGoalId ?? "none"}");
        if (spec.Baseline.Kind == ExperimentBaselineKind.GoalCohort)
        {
            Console.WriteLine($"baseline cohort goals: {string.Join(", ", spec.Baseline.BaselineGoalIds ?? [])}");
            Console.WriteLine($"comparison cohort goals: {string.Join(", ", spec.Baseline.ComparisonGoalIds ?? [])}");
        }
        Console.WriteLine($"metrics: {string.Join(", ", spec.Metrics)}");
        Console.WriteLine($"guardrail: {JsonSerializer.Serialize(spec.Guardrail, ExperimentStore.JsonOptions)}");
        Console.WriteLine($"stop rule target: {spec.StopRule.Count} {ExperimentReading.Name(spec.StopRule.Unit)}");
        foreach (var extension in spec.StopRule.Extensions ?? [])
            Console.WriteLine($"stop rule extension: {extension.FromCount} to {extension.NewCount} at {extension.ExtendedAt:O}: {extension.Reason}");
        Console.WriteLine($"decision rule: {JsonSerializer.Serialize(spec.DecisionRule, ExperimentStore.JsonOptions)}");
        Console.WriteLine($"epic id: {spec.EpicId ?? "none"}");
        Console.WriteLine($"decision evidence: {record.Decision?.Evidence ?? "none"}");
        Console.WriteLine($"decision action: {record.Decision?.Action ?? "none"}");
        Console.WriteLine($"decided at: {record.Decision?.DecidedAt:O}");
    }

}
