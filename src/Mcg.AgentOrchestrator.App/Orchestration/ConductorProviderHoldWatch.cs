using System.Globalization;
using System.Runtime.CompilerServices;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ConductorProviderHoldWatch
{
    private sealed record Announced(string SourceGoal, string SourceTask, DateTimeOffset PlacedAt);
    private static readonly ConditionalWeakTable<ConductorBatchLoop, ConductorProviderHoldWatch> Watches = new();
    private readonly ConductEventLogWriter? _writer;
    private readonly Dictionary<string, Announced> _announced = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<(string Binding, DateTimeOffset Due, int Attempt)> _probes = [];
    private bool _rehydrated;

    internal ConductorProviderHoldWatch(ConductEventLogWriter? writer) => _writer = writer;

    internal static ConductorProviderHoldWatch? For(ConductorBatchLoop loop, ConductEventLogWriter? writer) =>
        writer is null ? null : Watches.GetValue(loop, _ => new ConductorProviderHoldWatch(writer));

    internal static void Attach(ConductorBatchLoop loop, ConductorProviderHoldWatch watch) => Watches.Add(loop, watch);

    internal void ObserveTick(IReadOnlyCollection<Goal> scope, DateTimeOffset now)
    {
        try
        {
            if (_writer is null) return;
            if (!_rehydrated) { Rehydrate(); _rehydrated = true; }
            var held = ProviderUsageHoldView.Read(scope, now).ToDictionary(row => row.Hold.BindingKey, StringComparer.OrdinalIgnoreCase);
            foreach (var binding in _announced.Keys.Concat(held.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray())
            {
                if (!held.TryGetValue(binding, out var row))
                {
                    var old = _announced[binding];
                    var seconds = Math.Max(0, (long)(now - old.PlacedAt).TotalSeconds).ToString(CultureInfo.InvariantCulture);
                    _writer.Append("provider-hold-restored", old.SourceGoal,
                        $"PROVIDER_HOLD_RESTORED source={Prefix(old.SourceGoal)}/{Prefix(old.SourceTask)} placed_at={Instant(old.PlacedAt)} held_seconds={seconds} binding={binding}", timestamp: now);
                    _announced.Remove(binding);
                    continue;
                }

                var hold = row.Hold;
                if (!_announced.TryGetValue(binding, out var announced) || announced.PlacedAt != hold.PlacedAt)
                {
                    var schedule = row.Schedule;
                    _writer.Append("provider-hold-placed", hold.SourceGoalId.Value,
                        $"PROVIDER_HOLD_PLACED source={Prefix(hold.SourceGoalId.Value)}/{Prefix(hold.SourceTaskId.Value)} placed_at={Instant(hold.PlacedAt)} next_probe={(schedule is null ? "unavailable" : Instant(schedule.NextProbeAt))} reset_source={schedule?.ResetSource ?? "unavailable"} attempt={(schedule?.Attempt ?? 0).ToString(CultureInfo.InvariantCulture)} binding={binding}", timestamp: now);
                    _announced[binding] = new(hold.SourceGoalId.Value, hold.SourceTaskId.Value, hold.PlacedAt);
                }
                AnnounceProbe(scope, binding, now);
            }
        }
        catch (Exception error)
        {
            // A broken diagnostic sink must not make an observational watch fail the loop either.
            try { Console.Error.WriteLine($"PROVIDER_HOLD_WATCH_FAILED exception={error.GetType().Name} message={error.Message.ReplaceLineEndings("_").Replace(' ', '_').Replace('\t', '_')}"); }
            catch (Exception) { }
        }
    }

    private void AnnounceProbe(IReadOnlyCollection<Goal> scope, string binding, DateTimeOffset now)
    {
        foreach (var (goal, note) in scope.SelectMany(goal => goal.Timeline.Select(note => (goal, note)))
                     .OrderByDescending(pair => pair.note.OccurredAt))
        {
            if (!ProviderBudgetHoldSchedules.TryParseProbe(note, out var probe) || note.TaskId is not { } task ||
                !string.Equals(probe.BindingKey, binding, StringComparison.OrdinalIgnoreCase)) continue;
            var key = (binding.ToUpperInvariant(), probe.Due, probe.Attempt);
            if (!_probes.Contains(key))
            {
                _writer!.Append("provider-hold-probe", goal.Id.Value,
                    $"PROVIDER_HOLD_PROBE probe={Prefix(goal.Id.Value)}/{Prefix(task.Value)} due={Instant(probe.Due)} attempt={probe.Attempt.ToString(CultureInfo.InvariantCulture)} binding={binding}", timestamp: now);
                _probes.Add(key);
            }
            return;
        }
    }

    private void Rehydrate()
    {
        if (!File.Exists(_writer!.CurrentPath)) return;
        using var stream = new FileStream(_writer.CurrentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (!ConductEventFileSource.TryParse(line, out var evt) || evt is null ||
                OwnerProviderHoldNarration.Binding(evt) is not { Length: > 0 } binding) continue;
            if (evt.EventKind == "provider-hold-restored") _announced.Remove(binding);
            else if (evt.EventKind == "provider-hold-placed" &&
                     TryInstant(OwnerActivityNarrator.Field(evt, "placed_at"), out var placed) &&
                     OwnerActivityNarrator.Field(evt, "source")?.Split('/') is { Length: 2 } source &&
                     source.All(value => !string.IsNullOrWhiteSpace(value)) && !string.IsNullOrWhiteSpace(evt.GoalId))
                _announced[binding] = new(evt.GoalId, source[1], placed);
            else if (evt.EventKind == "provider-hold-probe" &&
                     TryInstant(OwnerActivityNarrator.Field(evt, "due"), out var due) &&
                     int.TryParse(OwnerActivityNarrator.Field(evt, "attempt"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var attempt) && attempt > 0)
                _probes.Add((binding.ToUpperInvariant(), due, attempt));
        }
    }

    private static bool TryInstant(string? text, out DateTimeOffset instant) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out instant);
    private static string Instant(DateTimeOffset instant) => ProviderBudgetHoldSchedules.FormatInstant(instant);
    private static string Prefix(string id) => id[..Math.Min(8, id.Length)];
}
