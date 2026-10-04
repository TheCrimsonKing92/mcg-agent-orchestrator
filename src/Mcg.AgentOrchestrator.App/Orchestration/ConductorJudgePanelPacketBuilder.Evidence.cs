using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorJudgePanelPacketBuilder
{
    private sealed record Evidence(IReadOnlyList<AcceptanceTrxFailure> Failures, IReadOnlyList<string> Notes);

    private Evidence ReadEvidence(PanelTrigger trigger)
    {
        var failures = new List<AcceptanceTrxFailure>();
        var notes = new List<string>();
        var references = trigger.ReceiptPaths.Concat(ReceiptReferences(trigger.Text))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var trxPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in references)
        {
            var path = Path.GetFullPath(reference, repositoryRoot);
            if (!AvailableAtTrigger(path, trigger.RecordedAt, notes)) continue;
            if (path.EndsWith(".trx", StringComparison.OrdinalIgnoreCase)) trxPaths.Add(path);
            else if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    // Open without write sharing while inspecting, so an in-place rewrite cannot
                    // sneak later content past the timestamp check.
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (!AvailableAtTrigger(path, trigger.RecordedAt, notes)) continue;
                    using var doc = JsonDocument.Parse(stream);
                    foreach (var trx in ReferencedTrx(doc.RootElement))
                        trxPaths.Add(Path.GetFullPath(trx, repositoryRoot));
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                { notes.Add("receipt unreadable: " + path); }
            }
        }
        foreach (var path in trxPaths.Order(StringComparer.Ordinal))
        {
            if (!AvailableAtTrigger(path, trigger.RecordedAt, notes)) continue;
            try
            {
                using var guard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (!AvailableAtTrigger(path, trigger.RecordedAt, notes)) continue;
                var receipt = AcceptanceTrxFailureReader.Read(path);
                if (receipt.Status == AcceptanceTrxReadStatus.Readable) failures.AddRange(receipt.Failures);
                else notes.Add($"receipt unreadable: {receipt.Status}: {path}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { notes.Add("receipt unreadable: " + path); }
        }
        return new(failures, notes);
    }

    private static bool AvailableAtTrigger(string path, DateTimeOffset trigger, List<string> notes)
    {
        if (!File.Exists(path)) { notes.Add("receipt unreadable: Missing: " + path); return false; }
        if (File.GetLastWriteTimeUtc(path) <= trigger.UtcDateTime) return true;
        notes.Add("receipt unavailable at trigger: " + path);
        return false;
    }

    private static IEnumerable<string> ReceiptReferences(string text)
    {
        var decoded = Uri.UnescapeDataString(text);
        const string reference = "(?:\"(?<path>[^\"]+\\.(?:trx|json))\"|'(?<path>[^']+\\.(?:trx|json))'|(?<path>[^\\s=;,|\"']+\\.(?:trx|json)))(?=$|[\\s.;,|\"'])";
        foreach (Match match in Regex.Matches(decoded, @"(?:pointer|result_path|receipt)\s*=\s*" + reference,
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            yield return match.Groups["path"].Value;
        // Bare TRX paths are already receipt references; arbitrary source JSON files are not.
        foreach (Match match in Regex.Matches(decoded,
                     "(?:\"(?<path>[^\"]+\\.trx)\"|'(?<path>[^']+\\.trx)'|(?<path>[^\\s=;,|\"']+\\.trx))(?=$|[\\s.;,|\"'])",
                     RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            yield return match.Groups["path"].Value;
    }

    private static IEnumerable<string> ReferencedTrx(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object)
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name.Equals("TestResultPaths", StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.Array)
                foreach (var item in property.Value.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { } path &&
                        path.EndsWith(".trx", StringComparison.OrdinalIgnoreCase)) yield return path;
            foreach (var path in ReferencedTrx(property.Value)) yield return path;
        }
        else if (root.ValueKind == JsonValueKind.Array)
            foreach (var item in root.EnumerateArray())
            foreach (var path in ReferencedTrx(item)) yield return path;
    }

    internal static string FirstFrame(string? stack) => stack?
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .FirstOrDefault(line => line.StartsWith("at ", StringComparison.Ordinal)) ?? "";

    internal static IReadOnlyList<string> ExplicitPaths(string text) => Regex.Matches(text,
            @"(?<![\w./\\:-])(?:[\w.-]+[/\\])+[\w.-]+\.[a-zA-Z0-9]+",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
        .Select(match => match.Value.Replace('\\', '/'))
        .Where(path => !Path.IsPathRooted(path) && !path.Split('/').Any(part => part is ".." or "."))
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private string Diff(PanelTrigger trigger, IReadOnlyList<string> paths)
    {
        static bool IsSha(string sha) => sha.Length is >= 12 and <= 40 && sha.All(Uri.IsHexDigit);
        if (!IsSha(trigger.BaseSha) || !IsSha(trigger.CandidateSha)) return "diff unavailable: revision unrecorded";
        if (paths.Count == 0) return "diff unavailable: no explicit repository-relative paths";
        if (diffProvider is not null) return diffProvider(trigger.BaseSha, trigger.CandidateSha, paths);
        var result = GitCli.Run(repositoryRoot, ["--literal-pathspecs", "diff", "--no-ext-diff", "--no-textconv",
            trigger.BaseSha, trigger.CandidateSha, "--", .. paths]);
        return result.Succeeded ? result.Output : "diff unavailable: git exit " + result.ExitCode;
    }

    private static IEnumerable<string> SplitHunks(string diff) =>
        Regex.Split(diff, @"(?m)(?=^diff --git |^@@ )", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
            .Where(part => part.Length > 0);
}
