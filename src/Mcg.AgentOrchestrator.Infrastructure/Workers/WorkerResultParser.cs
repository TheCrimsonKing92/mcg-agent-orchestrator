using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>
/// Format-lenient parser for WORKER_RESULT blocks.
/// Handles markdown decoration on the opener, end marker, and field keys,
/// and falls back to a field scan when no block opener is present.
/// Substance checks (commit reachability, test evidence, blockers) remain the
/// caller's responsibility and are NOT relaxed here.
/// </summary>
internal static class WorkerResultParser
{
    internal static readonly string[] RequiredFields =
        ["files", "commands", "tests", "blockers", "model_fit", "skills", "confidence"];

    // Strips *, #, ` from strings for opener/end-marker matching.
    private static readonly Regex MarkdownCharsPattern =
        new(@"[*#`]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Parses WORKER_RESULT fields from <paramref name="text"/> with format leniency:
    /// <list type="bullet">
    ///   <item>Markdown decoration on the opener, end marker, and field keys is stripped.</item>
    ///   <item>Missing END_WORKER_RESULT is tolerated (EOF / blank line / heading terminates).</item>
    ///   <item>When no opener is found, a field scan locates fields anywhere in the text.</item>
    /// </list>
    /// Returns false (with <paramref name="diagnostic"/>) when:
    /// <list type="bullet">
    ///   <item>A block opener is found but required fields are missing (substance gap, not format).</item>
    ///   <item>No opener and the field scan cannot recover the minimal viable receipt
    ///         (files + tests).</item>
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

        // No opener at all: scan the full text for known field patterns.
        // Minimal viable receipt requires at least commit, files, and tests.
        if (TryScanFields(lines, out fields))
        {
            return true;
        }

        diagnostic = "missing WORKER_RESULT block.";
        return false;
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

    private static bool TryScanFields(string[] lines, out Dictionary<string, string> fields)
    {
        fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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

        // Minimal viable receipt: files and tests must be recoverable. commit is advisory.
        return fields.ContainsKey("files") &&
               fields.ContainsKey("tests");
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
}
