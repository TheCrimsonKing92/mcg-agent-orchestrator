using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Suspension is derived from append-ordered receipts, never from a clock or a separate store.
internal static class SemanticJudgeSuspension
{
    internal const int RecentVerdictWindow = 10;
    internal const int ProbeCadence = 10;
    internal const string SuspendedErrorPrefix = "judge-suspended:";

    internal static string SuspendedError =>
        $"{SuspendedErrorPrefix} last {RecentVerdictWindow} recorded verdicts invalid; probing every {ProbeCadence}th landing";

    internal readonly record struct HistoryEntry(bool Valid, bool Suspended);
    internal enum Decision { Invoke, Probe, Suspended }

    internal static bool IsSuspendedError(IReadOnlyList<string> errors) =>
        errors.Count == 1 && errors[0].StartsWith(SuspendedErrorPrefix, StringComparison.Ordinal);

    internal static Dictionary<string, List<HistoryEntry>> ReadHistory(string ledgerPath)
    {
        var history = new Dictionary<string, List<HistoryEntry>>(StringComparer.Ordinal);
        try
        {
            foreach (var line in File.ReadLines(ledgerPath))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object
                        || !root.TryGetProperty("judges", out var judges)
                        || judges.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var entry in judges.EnumerateArray())
                    {
                        if (entry.ValueKind != JsonValueKind.Object
                            || !entry.TryGetProperty("judge", out var name)
                            || name.ValueKind != JsonValueKind.String
                            || !entry.TryGetProperty("valid", out var valid)
                            || valid.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        {
                            continue;
                        }

                        var suspended = !valid.GetBoolean()
                            && entry.TryGetProperty("errors", out var errors)
                            && errors.ValueKind == JsonValueKind.Array
                            && errors.GetArrayLength() == 1
                            && errors[0].ValueKind == JsonValueKind.String
                            && errors[0].GetString()!.StartsWith(SuspendedErrorPrefix, StringComparison.Ordinal);
                        var judgeName = name.GetString()!;
                        if (!history.TryGetValue(judgeName, out var entries))
                        {
                            history[judgeName] = entries = [];
                        }

                        entries.Add(new HistoryEntry(valid.GetBoolean(), suspended));
                    }
                }
                catch (JsonException)
                {
                    // Legacy/manual edits cannot establish a judge's suspension history.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Without readable history, retain the existing invocation behavior.
            history.Clear();
        }

        foreach (var entries in history.Values)
        {
            entries.Reverse();
        }

        return history;
    }

    internal static Decision Decide(IReadOnlyList<HistoryEntry> newestFirst)
    {
        var consecutiveSuspended = newestFirst.TakeWhile(entry => entry.Suspended).Count();
        // Synthetic suspension entries are skips, not invoked verdicts in the validity window.
        var verdicts = newestFirst.Where(entry => !entry.Suspended).Take(RecentVerdictWindow).ToList();
        if (verdicts.Count < RecentVerdictWindow || verdicts.Any(entry => entry.Valid))
        {
            return Decision.Invoke;
        }

        // Nine recorded skips make this landing the tenth eligible landing (the probe).
        return consecutiveSuspended >= ProbeCadence - 1 ? Decision.Probe : Decision.Suspended;
    }
}
