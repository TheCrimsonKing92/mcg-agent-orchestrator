using Mcg.AgentOrchestrator.Core;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>
/// Format-lenient parser for WORKER_RESULT blocks.
/// Handles markdown decoration on the opener, end marker, and field keys,
/// and falls back to a field scan when no block opener is present.
/// Substance checks (commit reachability and test evidence) remain the
/// caller's responsibility and are NOT relaxed here. The blockers field is
/// advisory context by default; callers may require an explicitly unblocked result.
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
        if (Array.Exists(lines, line => IsOpener(WorkerResultLineUnwrap.Unwrap(line.Trim()))))
        {
            return TryParseBlock(lines, out fields, out diagnostic) &&
                ValidateEvidenceBoundOutcomeFields(fields, out diagnostic);
        }

        // No opener at all: scan the full text for known field patterns. This
        // fallback is intentionally strict so incidental field labels in prose
        // cannot be treated as a successful worker result.
        if (TryScanFields(lines, out fields, out diagnostic))
        {
            return ValidateEvidenceBoundOutcomeFields(fields, out diagnostic);
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
        out string diagnostic,
        bool allowNoChangedFiles = false,
        bool requireNoBlockers = false,
        bool allowReadOnlyTestStatuses = false)
    {
        if (!TryParseResult(text, out var result, out diagnostic))
        {
            fields = [];
            return false;
        }

        fields = new Dictionary<string, string>(result.Fields, StringComparer.OrdinalIgnoreCase);
        if (!allowNoChangedFiles && !HasSubstantiveValue(fields, "files"))
        {
            diagnostic = "WORKER_RESULT has no changed files.";
            return false;
        }

        if (!HasSubstantiveValue(fields, "tests"))
        {
            diagnostic = "WORKER_RESULT has no completed test evidence.";
            return false;
        }

        if (allowReadOnlyTestStatuses)
        {
            if (result.TestsStatus is TestsStatus.Unknown or TestsStatus.Fail)
            {
                diagnostic = "WORKER_RESULT has no recognized non-failing read-only test status.";
                return false;
            }
        }
        else if (result.TestsStatus is TestsStatus.NotRun or TestsStatus.Inconclusive ||
                 (result.TestsStatus == TestsStatus.Unknown &&
                  (fields["tests"].Equals("not-run", StringComparison.OrdinalIgnoreCase) ||
                   fields["tests"].Equals("not run", StringComparison.OrdinalIgnoreCase))))
        {
            diagnostic = "WORKER_RESULT has no completed test evidence.";
            return false;
        }

        if (TestsReportFailure(result, out _))
        {
            diagnostic = result.TestsStatus == TestsStatus.Unknown
                ? $"WORKER_RESULT tests field is unstructured and its text reads as failure: {fields["tests"]}. Restate tests with a leading status word (pass, fail, not-run, deferred, inconclusive)."
                : $"WORKER_RESULT tests reported failure: {fields["tests"]}.";
            return false;
        }

        if (requireNoBlockers && result.BlockersStatus != BlockersStatus.None)
        {
            diagnostic = "WORKER_RESULT reports a blocker.";
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

        // The final block is authoritative because workers can emit a corrected
        // result after an earlier draft. Core outcome routing uses the same rule.
        var start = Array.FindLastIndex(lines, line => IsOpener(WorkerResultLineUnwrap.Unwrap(line.Trim())));
        if (start < 0)
        {
            diagnostic = "missing WORKER_RESULT block.";
            return false;
        }

        // Find end marker with the same leniency as the opener.
        var end = Array.FindIndex(lines, start + 1, line => IsEndMarker(WorkerResultLineUnwrap.Unwrap(line.Trim())));
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
            var line = WorkerResultLineUnwrap.Unwrap(lines[i].Trim());
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
            var trimmed = WorkerResultLineUnwrap.Unwrap(line.Trim());
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
            "inconclusive" when TryReadCanonicalEvidence(value, "inconclusive", out _) => TestsStatus.Inconclusive,
            _ => TestsStatus.Unknown
        };
    }

    private static bool TryReadCanonicalEvidence(string value, string token, out string evidence)
    {
        evidence = string.Empty;
        var prefix = $"{token} - ";
        var trimmed = value.Trim();
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        evidence = trimmed[prefix.Length..].Trim();
        return evidence.Length > 0;
    }

    private static bool ValidateEvidenceBoundOutcomeFields(
        IReadOnlyDictionary<string, string> fields,
        out string diagnostic)
    {
        diagnostic = string.Empty;
        if (fields.TryGetValue("tests", out var tests) &&
            string.Equals(ReadLeadingWorkerResultToken(tests), "inconclusive", StringComparison.Ordinal) &&
            !TryReadCanonicalEvidence(tests, "inconclusive", out _))
        {
            diagnostic = "WORKER_RESULT tests: inconclusive requires 'inconclusive - <current-round evidence>'.";
            return false;
        }

        if (fields.TryGetValue("blockers", out var blockers) &&
            string.Equals(ReadLeadingWorkerResultToken(blockers), "premise-invalid", StringComparison.Ordinal) &&
            !TryReadCanonicalEvidence(blockers, "premise-invalid", out _))
        {
            diagnostic = "WORKER_RESULT blockers: premise-invalid requires 'premise-invalid - <fact and evidence>'.";
            return false;
        }

        return true;
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

    internal static bool WorkerBuildCheckTestsReportSuccess(ParsedWorkerResult result)
    {
        var tests = result.Fields.TryGetValue("tests", out var value) ? value : string.Empty;
        if (string.IsNullOrWhiteSpace(tests) ||
            result.TestsStatus is TestsStatus.Fail or TestsStatus.NotRun or TestsStatus.Inconclusive ||
            WorkerBuildCheckTestsReportFailure(result, out _) ||
            Regex.IsMatch(tests, @"\b[1-9]\d*\s+errors?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
            Regex.IsMatch(tests, @"\b[1-9]\d*\s+error\(s\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return false;
        }

        var normalized = Regex.Replace(tests, @"\s+", " ").Trim();
        var zeroErrors = Regex.IsMatch(
            normalized,
            @"\b0\s+errors?\b|\b0\s+error\(s\)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        const string build = @"\bbuilds?\b";
        const string buildSubject = @"(?:\bbuilds?\b|Invoke-WorkerBuildCheck)";
        const string success = @"\b(?:pass(?:ed|es)?|succeed(?:ed|s)?|success(?:ful)?|ok|clean|green)\b";
        const string negation = @"\b(?:not|never|did\s+not|does\s+not|do\s+not|isn['’]?t|wasn['’]?t|weren['’]?t)\b";
        if (Regex.IsMatch(
                normalized,
                $@"{buildSubject}.{{0,40}}{negation}.{{0,20}}{success}|{negation}.{{0,20}}{success}.{{0,40}}{buildSubject}",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return false;
        }

        if (zeroErrors && normalized.Contains("Invoke-WorkerBuildCheck", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return (zeroErrors && Regex.IsMatch(normalized, build, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) ||
            Regex.IsMatch(normalized, $@"{build}.{{0,40}}{success}|{success}.{{0,40}}{build}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
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
