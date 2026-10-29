using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using Mcg.AgentOrchestrator.App.Orchestration;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed record OwnerConsoleEvidenceAttemptProgress(DateTimeOffset ObservedAt,
    int? Ordinal = null, DateTimeOffset? StartedAt = null, string? Phase = null, string? Lane = null,
    DateTimeOffset? LastProgressAt = null, int? Passed = null, int? Failed = null,
    OwnerConsoleEvidenceAttemptEnd? Previous = null);

// Observations of existing artifacts only; never used for scheduling or acceptance decisions.
internal sealed class OwnerConsoleEvidenceAttemptProgressReader(string storeRoot, TimeProvider clock)
{
    internal OwnerConsoleEvidenceAttemptProgress? Read(string goalId, OwnerConsoleGateProgress? observed)
    {
        var running = observed?.HostActivity?.LastOrDefault(run => run.GoalId == goalId &&
            !string.IsNullOrWhiteSpace(run.Attempt));
        if (running is null) return null;
        return Read(Path.Combine(storeRoot, goalId), goalId, running, clock.GetUtcNow()) with
        {
            Previous = OwnerConsoleHostEvidenceRuns.LatestEnded(observed?.EvidenceEnded, goalId, running.Attempt)
        };
    }

    internal static OwnerConsoleEvidenceAttemptProgress Read(string goalDirectory, string goalId,
        OwnerConsoleHostActivity running, DateTimeOffset now)
    {
        var attempt = running.Attempt;
        if (!Directory.Exists(goalDirectory) || running.GoalId != goalId || string.IsNullOrWhiteSpace(attempt) ||
            attempt.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return new(now);
        var prefix = Path.Combine(goalDirectory, attempt);
        var heartbeat = ReadJson(prefix + ".heartbeat.json", root => Text(root, "lastProgressAt"));
        DateTimeOffset? lastProgress = DateTimeOffset.TryParse(heartbeat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? at : null;
        var files = AttemptFiles(goalDirectory, goalId, prefix + ".out.log");
        var lane = files.Heartbeats
            .Select(path => ReadJson(path, root => Text(root, "goalId") == goalId ? Text(root, "currentTarget") : null))
            .Select(DescribeLane).Where(item => item.Name is not null)
            .OrderBy(item => item.Name, StringComparer.Ordinal).FirstOrDefault().Description;
        var counts = ReadCounts(files.Results);
        return new(now, running.Ordinal, running.StartedAt,
            AcceptanceAttemptOwnedPhaseTrail.ReadLastPhase(prefix + ".attempt.json"), lane, lastProgress,
            counts.Passed, counts.Failed);
    }

    private static (string[] Heartbeats, string[] Results) AttemptFiles(string directory, string goalId, string stdout)
    {
        try
        {
            // Focused verification owns a fresh results prefix, separate from the attempt id.
            // Its PHASE_PROGRESS lines are redirected to this attempt's stdout. Use those explicit
            // references rather than attributing same-goal files by timestamps or directory proximity.
            using var stream = OpenRead(stdout);
            using var reader = new StreamReader(stream);
            var lines = reader.ReadToEnd().Split('\n');
            var heartbeats = new HashSet<string>(StringComparer.Ordinal);
            var prefixes = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in lines.Take(lines.Length - 1)) // Ignore a partial trailing observation.
            {
                if (!line.StartsWith("PHASE_PROGRESS ", StringComparison.Ordinal)) continue;
                // Consume every token so a quoted target containing "heartbeat=" is not a reference.
                var tokens = Regex.Matches(line.TrimEnd('\r'),
                    "(?:^| )(?<key>[a-z_]+)=(?:\"(?<quoted>(?:\\\\\"|[^\"])*)\"|(?<plain>[^ \"]+))(?= |$)");
                var reference = tokens.Cast<Match>().FirstOrDefault(token => token.Groups["key"].Value == "heartbeat");
                if (reference is null) continue;
                var path = reference.Groups["quoted"].Success ? reference.Groups["quoted"].Value : reference.Groups["plain"].Value;
                if (!Path.IsPathFullyQualified(path) ||
                    !string.Equals(Path.GetDirectoryName(path), Path.TrimEndingDirectorySeparator(directory),
                        StringComparison.OrdinalIgnoreCase)) continue;
                var name = Path.GetFileName(path);
                var separator = name.IndexOf('.');
                if (separator < 0 || !name.EndsWith(".gate-heartbeat.json", StringComparison.Ordinal)) continue;
                var resultPrefix = name[..separator];
                if (!resultPrefix.StartsWith(goalId[..Math.Min(8, goalId.Length)] + "-pre-review-", StringComparison.Ordinal) ||
                    !resultPrefix.EndsWith("-candidate", StringComparison.Ordinal)) continue;
                heartbeats.Add(path);
                prefixes.Add(resultPrefix + ".");
            }
            var results = Directory.EnumerateFiles(directory, "*.trx").Where(path =>
                prefixes.Any(prefix => Path.GetFileName(path).StartsWith(prefix, StringComparison.Ordinal)))
                .Order(StringComparer.Ordinal).ToArray();
            return (heartbeats.Order(StringComparer.Ordinal).ToArray(), results);
        }
        catch (IOException) { return ([], []); }
        catch (UnauthorizedAccessException) { return ([], []); }
        catch (ArgumentException) { return ([], []); }
    }

    private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete);

    private static string? ReadJson(string path, Func<JsonElement, string?> select)
    {
        try
        {
            using var stream = OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            return select(document.RootElement);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
    }

    private static string? Text(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object
        ? root.EnumerateObject().FirstOrDefault(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .Value is { ValueKind: JsonValueKind.String } value ? value.GetString() : null : null;

    private static (string? Name, string? Description) DescribeLane(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return default;
        var labelAt = target.LastIndexOf(": ", StringComparison.Ordinal);
        var lane = (labelAt < 0 ? target : target[(labelAt + 2)..]).Trim();
        var space = lane.IndexOf(' ');
        var name = space < 0 ? lane : lane[..space];
        if (name.Length == 0) return default;
        if (space < 0) return (name, name);
        var classes = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in lane[(space + 1)..].Split('|'))
        {
            var filter = token.Trim();
            var separator = filter.IndexOfAny(['~', '=']);
            if (separator < 0 || filter[..separator] != "FullyQualifiedName" || filter.Contains('&')) return (name, name);
            var qualified = filter[(separator + 1)..];
            if (qualified.Length == 0 || qualified.Any(ch => !char.IsLetterOrDigit(ch) && ch is not ('_' or '.' or '+')))
                return (name, name);
            var shortName = qualified[(qualified.LastIndexOf('.') + 1)..];
            if (shortName.Length == 0) return (name, name);
            if (seen.Add(qualified)) classes.Add(shortName);
        }
        return (name, name + ": " + classes[0] + (classes.Count > 1
            ? " +" + (classes.Count - 1).ToString(CultureInfo.InvariantCulture) + " classes" : ""));
    }

    private static (int? Passed, int? Failed) ReadCounts(string[] paths)
    {
        if (paths.Length == 0) return default;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var passed = 0;
        var failed = 0;
        foreach (var path in paths)
        {
            var results = false;
            try
            {
                using var stream = OpenRead(path);
                using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null, IgnoreComments = true, IgnoreWhitespace = true });
                var testRun = false;
                var resultsDepth = -1;
                while (reader.Read())
                {
                    if (reader.NodeType == XmlNodeType.Element && reader.Depth == 0)
                        testRun = reader.LocalName == "TestRun";
                    if (testRun && reader.NodeType == XmlNodeType.Element && reader.Depth == 1 && reader.LocalName == "Results")
                    {
                        results = true;
                        resultsDepth = reader.IsEmptyElement ? -1 : reader.Depth;
                    }
                    else if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == resultsDepth)
                        resultsDepth = -1;
                    else if (resultsDepth >= 0 && reader.NodeType == XmlNodeType.Element &&
                        reader.Depth == resultsDepth + 1 && reader.LocalName == "UnitTestResult")
                    {
                        var outcome = reader.GetAttribute("outcome");
                        var execution = reader.GetAttribute("executionId");
                        var test = reader.GetAttribute("testId");
                        var identity = !string.IsNullOrEmpty(execution) ? "execution:" + execution :
                            !string.IsNullOrEmpty(test) ? "test:" + test : null;
                        // Finish this element before counting; the trailing result may still be being written.
                        using (var result = reader.ReadSubtree()) while (result.Read()) { }
                        if (identity is not null && !seen.Add(identity)) continue;
                        if (outcome == "Passed") passed++;
                        else if (outcome == "Failed") failed++;
                    }
                }
            }
            catch (XmlException) { } // Keep complete results from the trustworthy prefix of a streaming TRX.
            catch (IOException) { return default; }
            catch (UnauthorizedAccessException) { return default; }
            if (!results) return default;
        }
        return (passed, failed);
    }
}
