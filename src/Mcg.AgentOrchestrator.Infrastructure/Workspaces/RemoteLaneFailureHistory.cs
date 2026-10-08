using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record RemoteLaneFailureStreak(int Count, DateTimeOffset? NewestFailureAt);

// A streak belongs to a lane/filter binding and is derived from ledger append order.
internal sealed class RemoteLaneFailureHistory
{
    private static readonly HashSet<string> CountedOutcomes = new(StringComparer.OrdinalIgnoreCase)
    {
        "remote-red", "late-after-fallback", "trx-incomplete", "lease-expired",
        "lane-timeout", "unexpected-not-executed"
    };

    internal IReadOnlyDictionary<(string Lane, string Filter), RemoteLaneFailureStreak> Read(string path)
    {
        var streaks = new Dictionary<(string Lane, string Filter), RemoteLaneFailureStreak>();
        try
        {
            foreach (var line in SharedJsonlFile.ReadLines(path).TakeLast(GoalAcceptanceVerifier.RemoteLaneOfferHistoryLineLimit))
            {
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    var outcome = root.GetProperty("outcome").GetString();
                    var accepted = string.Equals(outcome, "accepted", StringComparison.OrdinalIgnoreCase);
                    if (!accepted && (outcome is null || !CountedOutcomes.Contains(outcome))) continue;
                    var lane = root.GetProperty("lane").GetString();
                    var filter = root.GetProperty("expected").GetProperty("filter").GetString();
                    if (string.IsNullOrWhiteSpace(lane) || string.IsNullOrWhiteSpace(filter)) continue;
                    var key = (lane, filter);
                    if (accepted)
                    {
                        streaks[key] = new(0, null);
                        continue;
                    }
                    var observedAt = root.GetProperty("observed_at").GetDateTimeOffset();
                    streaks.TryGetValue(key, out var previous);
                    var newest = previous?.NewestFailureAt is { } prior && prior > observedAt ? prior : observedAt;
                    streaks[key] = new((previous?.Count ?? 0) + 1, newest);
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new Dictionary<(string, string), RemoteLaneFailureStreak>(); }
        return streaks;
    }
}
