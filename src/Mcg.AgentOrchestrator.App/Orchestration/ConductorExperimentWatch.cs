using System.Globalization;
using System.Runtime.CompilerServices;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>Observes readings, submits flag reverts and records refutations, and confirms applied flag keeps with a make-permanent backlog item.</summary>
internal sealed class ConductorExperimentWatch
{
    private const string KeyPrefix = "experiment-reading-due:";
    private static readonly string[] Triggers = ["stop-rule", "guardrail"];
    private static readonly ConditionalWeakTable<ConductorBatchLoop, ConductorExperimentWatch> Watches = new();
    private readonly OrchestratorWorkspace _workspace;
    private readonly ConductEventLogWriter? _writer;
    private readonly Func<ICollaborationItemStore> _collaborationStore;
    private readonly HashSet<(string Id, string Trigger)> _raised = [];
    private bool _rehydrated;

    internal ConductorExperimentWatch(OrchestratorWorkspace workspace, ConductEventLogWriter? writer,
        Func<ICollaborationItemStore>? collaborationStore = null)
    {
        _workspace = workspace;
        _writer = writer;
        _collaborationStore = collaborationStore ?? (() => CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory));
    }

    internal static ConductorExperimentWatch? For(ConductorBatchLoop loop, OrchestratorWorkspace? workspace,
        ConductEventLogWriter? writer) => workspace is null ? null :
        Watches.GetValue(loop, _ => new ConductorExperimentWatch(workspace, writer));

    internal static void Attach(ConductorBatchLoop loop, ConductorExperimentWatch watch) => Watches.Add(loop, watch);

    internal void ObserveTick(DateTimeOffset asOf)
    {
        try
        {
            if (!File.Exists(_workspace.ExperimentStorePath)) return;
            var experiments = new ExperimentStore(_workspace.ExperimentStorePath);
            var open = experiments.ListOpenAsync().GetAwaiter().GetResult();
            var items = _collaborationStore();
            if (!_rehydrated)
            {
                // Terminal items also prove a signal was raised: acknowledging a question must not
                // re-arm the same experiment/trigger when the conductor restarts.
                foreach (var item in items.ListAsync().GetAwaiter().GetResult())
                {
                    if (item.Type != CollaborationItemType.Decision || item.GoalId is not null ||
                        item.CorrelationKey is not { } key || !key.StartsWith(KeyPrefix, StringComparison.Ordinal)) continue;
                    var parts = key[KeyPrefix.Length..].Split(':');
                    if (parts.Length == 2 && parts[0].Length == 32 && Triggers.Contains(parts[1]))
                        _raised.Add((parts[0], parts[1]));
                }
                _rehydrated = true;
            }

            var openIds = open.Select(record => record.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var id in _raised.Select(pair => pair.Id).Distinct().Where(id => !openIds.Contains(id)).ToArray())
            {
                try
                {
                    var record = experiments.ResolveAsync(id).GetAwaiter().GetResult();
                    if (record is null || record.Outcome == ExperimentOutcomeState.Open) continue;
                    foreach (var trigger in Triggers)
                        items.TryResolveAsync(Key(id, trigger), $"experiment {id} decided: {ExperimentReading.Name(record.Outcome)}")
                            .GetAwaiter().GetResult();
                    _raised.RemoveWhere(pair => pair.Id == id);
                }
                catch (Exception error) { LogFailure(error); }
            }

            if (open.Count == 0) return;
            // The production loop kernel excludes terminal goals; experiment readings need the
            // same durable history as experiment-show, loaded afresh without a writer transaction.
            var goals = ExperimentGoals.Read(_workspace.SqliteStatePath);
            var landings = ExperimentLandingTimes.Read(_workspace.GoalLifecycleEventsDirectory);
            IReadOnlyCollection<DateTimeOffset> gateAttempts = open.Any(record => record.Spec.StopRule.Unit == ExperimentStopUnit.Gates)
                ? ExperimentGateAttempts.Read(_workspace.ConductEventsLogPath) : [];
            var intents = File.Exists(Path.Combine(_workspace.OrchestratorDirectory, SqliteOperatorIntentStore.DatabaseFileName))
                ? CliOwnerDigestRetryIntents.Read(_workspace, asOf) : [];
            var policyPath = Path.Combine(_workspace.OrchestratorDirectory, "conductor-policy.json");
            var reverts = new ConductorExperimentFlagRevertController(experiments,
                () => SqliteOperatorIntentStore.ForDirectories(_workspace.OrchestratorDirectory, _workspace.LogDirectory),
                policyPath);
            var keeps = new ConductorExperimentFlagKeepController(experiments,
                () => new BacklogStore(_workspace.BacklogStorePath), policyPath);
            foreach (var record in open)
            {
                try
                {
                    var reading = ExperimentReading.Evaluate(record, goals, landings, intents, asOf,
                        ExperimentGateAttempts.Count(gateAttempts, ExperimentReading.ComparisonStart(record), asOf));
                    try { if (reverts.TryRevert(record, reading, asOf)) continue; }
                    catch (Exception error) { LogFailure(error); }
                    try { if (keeps.TryKeep(record, reading, asOf)) continue; }
                    catch (Exception error) { LogFailure(error); }
                    foreach (var trigger in Triggers)
                    {
                        if (!(trigger == "stop-rule" ? reading.StopRuleMet : reading.GuardrailBreached) ||
                            _raised.Contains((record.Id, trigger))) continue;
                        try
                        {
                            items.RaiseAsync(CollaborationItemType.Decision, null,
                                $"Experiment {record.Id[..8]} reading due ({trigger})",
                                $"Experiment {record.Id} reached {trigger}. Run experiment-show {record.Id} and record the result with " +
                                $"experiment-decide {record.Id} --outcome <confirmed|refuted|inconclusive> --evidence <reference> --action <text>.",
                                Key(record.Id, trigger)).GetAwaiter().GetResult();
                            // Raise is retryable; after it succeeds attempt the log append only once,
                            // like the capacity watch, even when that append fails.
                            _raised.Add((record.Id, trigger));
                            var observed = reading.ObservedCount?.ToString(CultureInfo.InvariantCulture) ?? "unavailable";
                            _writer?.Append("experiment-reading-due", null,
                                $"EXPERIMENT_READING_DUE experiment={record.Id} trigger={trigger} observed={observed} " +
                                $"stopRuleMet={Flag(reading.StopRuleMet)} verdict={reading.Verdict} guardrailBreached={Flag(reading.GuardrailBreached)}", asOf);
                        }
                        catch (Exception error) { LogFailure(error); }
                    }
                }
                catch (Exception error) { LogFailure(error); }
            }
        }
        catch (Exception error) { LogFailure(error); }
    }

    private static string Key(string id, string trigger) => $"{KeyPrefix}{id}:{trigger}";
    private static string Flag(bool value) => value ? "true" : "false";
    private static void LogFailure(Exception error) => Console.Error.WriteLine(
        $"EXPERIMENT_WATCH_FAILED exception={error.GetType().Name} message={error.Message.ReplaceLineEndings("_").Replace(' ', '_').Replace('\t', '_')}");
}
