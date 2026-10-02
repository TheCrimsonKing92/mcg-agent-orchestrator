using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// A watch belongs to one immutable landing, not to a dispatch or worker round.
internal sealed record PromptRolloutWatch(
    string LandingSha,
    string LandingGoalId,
    DateTimeOffset LandedAt,
    IReadOnlyList<string> Phrases,
    IReadOnlyList<string> MatchedGoalIds,
    IReadOnlyList<string> MatchedPhrases,
    IReadOnlyList<string> CountedRoundKeys,
    string State = "open",
    int Schema = 1);

internal sealed class PromptRolloutWatchStore(string path)
{
    internal const string FileName = "prompt-rollout-watches.jsonl";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal IReadOnlyList<PromptRolloutWatch> Load(Action<string> diagnostic)
    {
        var watches = new Dictionary<string, PromptRolloutWatch>(StringComparer.Ordinal);
        if (!File.Exists(path)) return [];
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var watch = JsonSerializer.Deserialize<PromptRolloutWatch>(line, JsonOptions);
                if (watch is null || watch.Schema != 1 || string.IsNullOrWhiteSpace(watch.LandingSha) ||
                    string.IsNullOrWhiteSpace(watch.LandingGoalId) || watch.Phrases is not { Count: > 0 } ||
                    watch.Phrases.Any(string.IsNullOrWhiteSpace) || watch.MatchedGoalIds is null ||
                    watch.MatchedPhrases is null || watch.CountedRoundKeys is null ||
                    watch.State is not ("open" or "fired" or "exhausted"))
                {
                    diagnostic("PROMPT_ROLLOUT_WATCH result=skipped reason=invalid-record");
                    continue;
                }
                watches[watch.LandingSha] = watch;
            }
            catch (JsonException)
            {
                diagnostic("PROMPT_ROLLOUT_WATCH result=skipped reason=malformed-record");
            }
        }
        return watches.Values.ToArray();
    }

    internal void Append(PromptRolloutWatch watch)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        // Separate a previously interrupted trailing record from the next complete record.
        var bytes = Encoding.UTF8.GetBytes("\n" + JsonSerializer.Serialize(watch, JsonOptions) + "\n");
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}
