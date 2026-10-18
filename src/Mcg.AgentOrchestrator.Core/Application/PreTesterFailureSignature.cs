using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

public static class PreTesterFailureSignature
{
    public static string Describe(string? message, string? stackTrace)
    {
        var first = Lines(message).FirstOrDefault(line => line.Length > 0) ?? "no-message";
        if (first.Length > 200) first = first[..200];
        var frame = "no-frame";
        foreach (var line in stackTrace?.Split(['\r', '\n']) ?? [])
        {
            var match = Regex.Match(line, @" in (?<path>.+):line (?<n>\d+)\s*$");
            if (!match.Success) continue;
            var path = match.Groups["path"].Value.Replace('\\', '/');
            frame = path[(path.LastIndexOf('/') + 1)..] + ":" + match.Groups["n"].Value;
            break;
        }
        return first + " @ " + frame;
    }

    public static string AppendField(string indexLine, IReadOnlyList<KeyValuePair<string, string>> signatures) =>
        signatures.Count == 0 ? indexLine : indexLine + "; failing_signatures=" +
            string.Join(',', signatures.Select(pair =>
                Uri.EscapeDataString(pair.Key) + "|" + Uri.EscapeDataString(pair.Value)));

    public static PreTesterRedRun ReadRun(string indexLine)
    {
        var entry = PreTesterEvidenceIndexLines.Parse(indexLine);
        var signatures = new Dictionary<string, string>(StringComparer.Ordinal);
        if (entry is null) return new([], signatures);
        var field = indexLine.Split(';', StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2))
            .FirstOrDefault(parts => parts.Length == 2 && parts[0] == "failing_signatures");
        if (field is not null)
        {
            foreach (var encoded in field[1].Split(','))
            {
                var pair = encoded.Split('|');
                if (pair.Length != 2 || pair[0].Length == 0 || pair[1].Length == 0) continue;
                try
                {
                    signatures.TryAdd(Uri.UnescapeDataString(pair[0]), Uri.UnescapeDataString(pair[1]));
                }
                catch (UriFormatException) { /* Missing detail preserves name-only comparison. */ }
            }
        }
        return new(entry.FailingTests, signatures);
    }

    private static string[] Lines(string? value) =>
        value?.Split(['\r', '\n'], StringSplitOptions.TrimEntries) ?? [];
}
