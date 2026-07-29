using Mcg.AgentOrchestrator.Core;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>
/// Format-lenient parser for WORKER_RESULT blocks.
/// Handles markdown decoration on the opener, end marker, and field keys,
/// and falls back to a field scan when no block opener is present.
/// Substance checks (commit reachability and test evidence) remain the
/// caller's responsibility and are NOT relaxed here. The blockers field is
/// advisory context for otherwise successful changed work.
/// </summary>
internal static class WorkerResultParser
{
    internal enum TestsStatus
    {
        Unknown,
        Pass,
        Fail,
        NotRun,
        Deferred,
        Inconclusive
    }

    internal enum BlockersStatus
    {
        Unknown,
        None,
        Present
    }

    internal sealed record ParsedWorkerResult(
        IReadOnlyDictionary<string, string> Fields,
        TestsStatus TestsStatus,
        BlockersStatus BlockersStatus);

    internal static readonly string[] RequiredFields =
        [.. AgentOutputDirectives.WorkerResultFieldNames];

    // Strips *, #, ` from strings for opener/end-marker matching.
    private static readonly Regex MarkdownCharsPattern =
        new(@"[*#`]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Parses WORKER_RESULT fields from <paramref name="text"/> with format leniency:
    /// <list type="bullet">
    ///   <item>Markdown decoration on the opener, end marker, and field keys is stripped.</item>
    ///   <item>Missing END_WORKER_RESULT is tolerated (EOF / blank line / heading terminates).</item>
    ///   <item>When no opener is found, a field scan locates the full required field set anywhere in the text.</item>
    /// </list>
    /// Returns false (with <paramref name="diagnostic"/>) when:
    /// <list type="bullet">
    ///   <item>A block opener is found but required fields are missing (substance gap, not format).</item>
    ///   <item>No opener and the field scan cannot recover the full required field set.</item>
    /// </list>
    /// </summary>
    public static bool TryParseFields(
        string text,
        out Dictionary<string, string> fields,
        out string diagnostic)
    {
        fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        diagnostic = string.Empty;

        var lines = text.Replace("\r\n", "\n").Split('\n');

        // If a block opener exists (even with markdown decoration), parse the block.
        // An opener implies intent: missing fields are reported as substance failures.
        if (Array.Exists(lines, line => IsOpener(line.Trim())))
        {
            return TryParseBlock(lines, out fields, out diagnostic);
        }

        // No opener at all: scan the full text for known field patterns. This
        // fallback is intentionally strict so incidental field labels in prose
        // cannot be treated as a successful worker result.
        if (TryScanFields(lines, out fields, out diagnostic))
        {
            return true;
        }

        return false;
    }

    internal static bool TryParseResult(
        string text,
        out ParsedWorkerResult result,
        out string diagnostic)
    {
        result = new ParsedWorkerResult(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            TestsStatus.Unknown,
            BlockersStatus.Unknown);

        if (!TryParseFields(text, out var fields, out diagnostic))
        {
            return false;
        }

        var testsStatus = fields.TryGetValue("tests", out var tests)
            ? ParseTestsStatus(tests)
            : TestsStatus.Unknown;
        var blockersStatus = fields.TryGetValue("blockers", out var blockers)
            ? ParseBlockersStatus(blockers)
            : BlockersStatus.Unknown;

        result = new ParsedWorkerResult(fields, testsStatus, blockersStatus);
        return true;
    }

    internal static bool TryParseSuccessfulResult(
        string text,
        out Dictionary<string, string> fields,
        out string diagnostic)
    {
        if (!TryParseResult(text, out var result, out diagnostic))
        {
            fields = [];
            return false;
        }

        fields = new Dictionary<string, string>(result.Fields, StringComparer.OrdinalIgnoreCase);
        if (!HasSubstantiveValue(fields, "files"))
        {
            diagnostic = "WORKER_RESULT has no changed files.";
            return false;
        }

        if (!HasSubstantiveValue(fields, "tests") ||
            result.TestsStatus is TestsStatus.NotRun or TestsStatus.Inconclusive ||
            (result.TestsStatus == TestsStatus.Unknown &&
             (fields["tests"].Equals("not-run", StringComparison.OrdinalIgnoreCase) ||
              fields["tests"].Equals("not run", StringComparison.OrdinalIgnoreCase))))
        {
            diagnostic = "WORKER_RESULT has no completed test evidence.";
            return false;
        }

        if (TestsReportFailure(result, out _))
        {
            diagnostic = $"WORKER_RESULT tests reported failure: {fields["tests"]}.";
            return false;
        }

        diagnostic = string.Empty;
        return true;
    }

    // ── Block parser ──────────────────────────────────────────────────────────

    private static bool TryParseBlock(
        string[] lines,
        out Dictionary<string, string> fields,
        out string diagnostic)
    {
        fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        diagnostic = string.Empty;

        var start = Array.FindIndex(lines, line => IsOpener(line.Trim()));
        if (start < 0)
        {
            diagnostic = "missing WORKER_RESULT block.";
            return false;
        }

        // Find end marker with the same leniency as the opener.
        var end = Array.FindIndex(lines, start + 1, line => IsEndMarker(line.Trim()));
        if (end < 0)
        {
            // Tolerate missing END marker: blank line, markdown heading, or EOF terminates.
            end = Array.FindIndex(lines, start + 1, line =>
            {
                var t = line.Trim();
                return t.Length == 0 || t.StartsWith('#');
            });
            if (end < 0)
            {
                end = lines.Length;
            }
        }

        for (var i = start + 1; i < end; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var sep = line.IndexOf(':', StringComparison.Ordinal);
            if (sep <= 0)
            {
                continue;
            }

            var key = NormalizeKey(line[..sep]);
            if (!string.IsNullOrWhiteSpace(key))
            {
                // Last-writer-wins within the block (matches original behavior).
                fields[key] = line[(sep + 1)..].Trim();
            }
        }

        // Avoid lambdas over the out parameter (CS1628).
        var missing = new List<string>();
        foreach (var f in RequiredFields)
        {
            if (!fields.ContainsKey(f))
            {
                missing.Add(f);
            }
        }

        if (missing.Count > 0)
        {
            diagnostic = $"missing field(s): {string.Join(", ", missing)}.";
            return false;
        }

        return true;
    }

    // ── Field scan (no-opener fallback) ───────────────────────────────────────

    private static bool TryScanFields(
        string[] lines,
        out Dictionary<string, string> fields,
        out string diagnostic)
    {
        fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        diagnostic = string.Empty;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            var sep = trimmed.IndexOf(':', StringComparison.Ordinal);
            if (sep <= 0)
            {
                continue;
            }

            var key = NormalizeKey(trimmed[..sep]);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (Array.Exists(RequiredFields, f =>
                    string.Equals(f, key, StringComparison.OrdinalIgnoreCase)) &&
                !fields.ContainsKey(key))
            {
                // First-wins: take the first occurrence of each field.
                fields[key] = trimmed[(sep + 1)..].Trim();
            }
        }

        var missing = new List<string>();
        foreach (var f in RequiredFields)
        {
            if (!fields.ContainsKey(f))
            {
                missing.Add(f);
            }
        }

        if (missing.Count == 0)
        {
            return true;
        }

        diagnostic = fields.Count == 0
            ? "missing WORKER_RESULT block."
            : $"missing WORKER_RESULT field(s): {string.Join(", ", missing)}.";
        return false;
    }

    // ── Normalization helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Returns true when <paramref name="trimmedLine"/> is a WORKER_RESULT block opener,
    /// tolerating markdown decoration such as <c>**WORKER_RESULT**:</c> or <c>## WORKER_RESULT:</c>.
    /// </summary>
    internal static bool IsOpener(string trimmedLine)
    {
        var normalized = MarkdownCharsPattern.Replace(trimmedLine, "").Trim().TrimEnd(':');
        return string.Equals(normalized, "WORKER_RESULT", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true when <paramref name="trimmedLine"/> is a WORKER_RESULT end marker,
    /// tolerating markdown decoration such as <c>**END_WORKER_RESULT**</c>.
    /// </summary>
    internal static bool IsEndMarker(string trimmedLine)
    {
        var normalized = MarkdownCharsPattern.Replace(trimmedLine, "").Trim();
        return string.Equals(normalized, "END_WORKER_RESULT", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Normalizes a field key by stripping markdown decoration characters
    /// (<c>*</c>, <c>#</c>, <c>`</c>) and a leading bullet dash (<c>-</c>).
    /// </summary>
    internal static string NormalizeKey(string key)
    {
        var noMarkdown = MarkdownCharsPattern.Replace(key.Trim(), "");
        return noMarkdown.TrimStart('-', ' ').Trim();
    }

    internal static TestsStatus ParseTestsStatus(string value)
    {
        return ReadLeadingWorkerResultToken(value) switch
        {
            "pass" => TestsStatus.Pass,
            "fail" => TestsStatus.Fail,
            "not-run" => TestsStatus.NotRun,
            "deferred" => TestsStatus.Deferred,
            "inconclusive" => TestsStatus.Inconclusive,
            _ => TestsStatus.Unknown
        };
    }

    internal static BlockersStatus ParseBlockersStatus(string value)
    {
        var token = ReadLeadingWorkerResultToken(value);
        if (token.Length == 0)
        {
            return BlockersStatus.Unknown;
        }

        return string.Equals(token, "none", StringComparison.OrdinalIgnoreCase)
            ? BlockersStatus.None
            : BlockersStatus.Present;
    }

    internal static bool TestsReportFailure(ParsedWorkerResult result, out string tests)
    {
        tests = result.Fields.TryGetValue("tests", out var value) ? value : string.Empty;
        if (result.TestsStatus == TestsStatus.Fail)
        {
            return true;
        }

        if (result.TestsStatus != TestsStatus.Unknown)
        {
            tests = string.Empty;
            return false;
        }

        if (LegacyTestsReportFailure(tests))
        {
            return true;
        }

        tests = string.Empty;
        return false;
    }

    internal static bool WorkerBuildCheckTestsReportFailure(ParsedWorkerResult result, out string tests)
    {
        tests = result.Fields.TryGetValue("tests", out var value) ? value : string.Empty;
        if (!tests.Contains("Invoke-WorkerBuildCheck", StringComparison.OrdinalIgnoreCase))
        {
            tests = string.Empty;
            return false;
        }

        if (result.TestsStatus == TestsStatus.Fail)
        {
            return true;
        }

        if (result.TestsStatus != TestsStatus.Unknown)
        {
            tests = string.Empty;
            return false;
        }

        if (tests.Contains("0 errors", StringComparison.OrdinalIgnoreCase) ||
            tests.Contains("0 error(s)", StringComparison.OrdinalIgnoreCase))
        {
            tests = string.Empty;
            return false;
        }

        if (LegacyTestsReportFailure(tests) ||
            Regex.IsMatch(tests, @"\b[1-9]\d*\s+errors?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
            Regex.IsMatch(tests, @"\b[1-9]\d*\s+error\(s\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return true;
        }

        tests = string.Empty;
        return false;
    }

    private static bool HasSubstantiveValue(IReadOnlyDictionary<string, string> fields, string key)
    {
        return fields.TryGetValue(key, out var value) &&
            !string.IsNullOrWhiteSpace(value) &&
            !value.Equals("none", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LegacyTestsReportFailure(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            return false;
        }

        if (!normalized.Contains("fail", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !normalized.Contains("failed: 0", StringComparison.OrdinalIgnoreCase) &&
            !normalized.Contains("failures: 0", StringComparison.OrdinalIgnoreCase) &&
            !normalized.Contains("0 failed", StringComparison.OrdinalIgnoreCase) &&
            !normalized.Contains("0 failures", StringComparison.OrdinalIgnoreCase) &&
            !normalized.Contains("no failures", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadLeadingWorkerResultToken(string value)
    {
        var trimmed = value.Trim();
        var length = 0;
        while (length < trimmed.Length)
        {
            var ch = trimmed[length];
            if (char.IsLetterOrDigit(ch) || ch == '-')
            {
                length++;
                continue;
            }

            break;
        }

        return length == 0 ? string.Empty : trimmed[..length].ToLowerInvariant();
    }
}
