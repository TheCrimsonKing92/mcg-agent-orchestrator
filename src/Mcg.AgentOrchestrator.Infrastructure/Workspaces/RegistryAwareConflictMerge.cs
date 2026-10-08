using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Three-way entry reconciliation for explicitly named registries; git remains the caller's concern.</summary>
internal static class RegistryAwareConflictMerge
{
    internal const string SameKeyConflict = "registry-same-key-conflict";
    internal const string CeilingBelowMeasured = "registry-ceiling-below-measured";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private sealed record RegistryStrategy(Regex RowPattern, Func<Match, string> Key,
        Func<SyntaxNode, IEnumerable<int>> EntryStarts, Func<string, bool> IsEntryCollection,
        Func<Entry, bool> CanRemeasure);

    private static readonly IReadOnlyDictionary<string, RegistryStrategy> Registries = new Dictionary<string, RegistryStrategy>(StringComparer.Ordinal)
    {
        [SourceSizeRatchet.SourcePath] = new(new Regex(
            "^\\s*new\\s+(?<kind>SourceSizeCeiling|SourceClassCeiling)\\(\\s*\"(?<identity>[^\"]+)\"\\s*,\\s*(?<ceiling>\\d+)(?:\\s*,\\s*\\d+)?\\s*\\)\\s*,?\\s*(?://.*)?$",
            RegexOptions.CultureInvariant),
            match => match.Groups["kind"].Value + ":" + match.Groups["identity"].Value,
            syntax => syntax.DescendantNodes().OfType<ObjectCreationExpressionSyntax>()
                .Where(node => node.Type.ToString() is "SourceSizeCeiling" or "SourceClassCeiling").Select(node => node.SpanStart),
            type => type.Contains("SourceSizeCeiling", StringComparison.Ordinal) || type.Contains("SourceClassCeiling", StringComparison.Ordinal),
            entry => entry.Key.StartsWith("SourceSizeCeiling:", StringComparison.Ordinal))
    };

    internal sealed record Entry(string Key, string Identity, string[] Comments, string Row, bool Remeasure = false)
    {
        internal string Content => string.Join('\n', Comments.Append(Row));
    }

    private sealed record Document(List<Entry> Entries, List<string> Order, Dictionary<string, string> Skeleton)
    {
        internal string SkeletonText => string.Concat(Skeleton.Values);
    }

    internal sealed record Plan(string Path, List<Entry> Entries, Dictionary<string, string> Skeleton,
        string[] BaseOrder, Dictionary<string, List<string>> Additions, string LineEnding, bool Bom);

    internal static bool IsRegistry(string path) => Registries.ContainsKey(path);

    // null plan + reason=none means decline: the existing hunk resolver still owns this conflict.
    internal static Plan? TryPlan(string path, string baseText, string mainText, string branchText,
        byte[] workingBytes, out string reason)
    {
        reason = "none";
        if (!Registries.TryGetValue(path, out var strategy)) return null;
        var bom = workingBytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf });
        var workingText = Utf8.GetString(workingBytes, bom ? 3 : 0, workingBytes.Length - (bom ? 3 : 0));
        if (workingText.Contains('\0')) return null;
        var baseline = Parse(baseText, strategy);
        var main = Parse(mainText, strategy);
        var branch = Parse(branchText, strategy);
        if (baseline is null || main is null || branch is null) return null;
        var skeleton = Reconcile(baseline.SkeletonText, main.SkeletonText, branch.SkeletonText, out var outsideConflict);
        if (outsideConflict) return null;
        var layout = skeleton == baseline.SkeletonText ? baseline : skeleton == main.SkeletonText ? main : branch;
        var originals = baseline.Entries.ToDictionary(entry => entry.Key, StringComparer.Ordinal);
        var mains = main.Entries.ToDictionary(entry => entry.Key, StringComparer.Ordinal);
        var branches = branch.Entries.ToDictionary(entry => entry.Key, StringComparer.Ordinal);
        var entries = new List<Entry>();
        foreach (var key in originals.Keys.Concat(mains.Keys).Concat(branches.Keys).Distinct(StringComparer.Ordinal))
        {
            originals.TryGetValue(key, out var old);
            mains.TryGetValue(key, out var upstream);
            branches.TryGetValue(key, out var goal);
            var content = Reconcile(old?.Content, upstream?.Content, goal?.Content, out var conflict);
            if (!conflict)
            {
                if (content is not null) entries.Add(content == upstream?.Content ? upstream! : goal!);
                continue;
            }
            if (upstream is null || goal is null || !strategy.CanRemeasure(upstream))
            {
                reason = SameKeyConflict;
                return null;
            }
            entries.Add(upstream with
            {
                Comments = [.. upstream.Comments, .. goal.Comments.Where(line => !upstream.Comments.Contains(line, StringComparer.Ordinal))],
                Remeasure = true
            });
        }
        var baseOrder = baseline.Order.ToArray();
        var baseKeys = baseOrder.ToHashSet(StringComparer.Ordinal);
        var additions = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var emitted = new HashSet<string>(baseOrder, StringComparer.Ordinal);
        foreach (var side in new[] { main, branch })
        {
            var anchor = "";
            foreach (var key in side.Order)
            {
                if (baseKeys.Contains(key)) anchor = key;
                else if (key.StartsWith("@region:", StringComparison.Ordinal)) return null;
                else if (emitted.Add(key))
                {
                    if (!additions.TryGetValue(anchor, out var keys)) additions[anchor] = keys = [];
                    keys.Add(key);
                }
            }
        }
        // Map a chosen side's non-entry text to base anchors, including text following additions.
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        var preceding = "";
        foreach (var (key, text) in layout.Skeleton)
        {
            if (baseKeys.Contains(key)) preceding = key;
            sections[preceding] = sections.GetValueOrDefault(preceding, "") + text;
        }
        return new(path, entries, sections, baseOrder, additions,
            workingText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : workingText.Contains('\r') ? "\r" : "\n", bom);
    }

    private static string? Reconcile(string? baseline, string? main, string? branch, out bool conflict)
    {
        conflict = false;
        if (main == branch || branch == baseline) return main;
        if (main == baseline) return branch;
        conflict = true;
        return null;
    }

    private static Document? Parse(string text, RegistryStrategy strategy)
    {
        text = text.TrimStart('\uFEFF').ReplaceLineEndings("\n");
        if (text.Contains('\0') || text.Contains('\uFFFD')) return null;
        // Syntax ownership prevents row-shaped text in block comments or strings becoming entries.
        var tree = CSharpSyntaxTree.ParseText(text);
        if (tree.GetDiagnostics().Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)) return null;
        var syntax = tree.GetRoot();
        var entryStarts = strategy.EntryStarts(syntax).ToHashSet();
        var lines = text.Split('\n');
        var offsets = new int[lines.Length];
        for (var i = 1; i < lines.Length; i++) offsets[i] = offsets[i - 1] + lines[i - 1].Length + 1;
        // Array-start anchors keep additions inside their owning registry, including an empty array.
        var regions = new Dictionary<int, string>();
        foreach (var initializer in syntax.DescendantNodes().OfType<InitializerExpressionSyntax>())
        {
            var property = initializer.Ancestors().OfType<PropertyDeclarationSyntax>().FirstOrDefault();
            var variable = initializer.Ancestors().OfType<VariableDeclaratorSyntax>().FirstOrDefault();
            var ownerType = property?.Type.ToString() ?? (variable?.Parent as VariableDeclarationSyntax)?.Type.ToString();
            if (ownerType is null || !strategy.IsEntryCollection(ownerType)) continue;
            var owner = property?.Identifier.ValueText ?? variable!.Identifier.ValueText;
            var line = Array.FindIndex(offsets, offset => offset > initializer.OpenBraceToken.SpanStart);
            if (line < 0 || !regions.TryAdd(line, "@region:" + owner)) return null;
        }
        var entries = new List<Entry>();
        var order = new List<string>();
        var skeleton = new Dictionary<string, string>(StringComparer.Ordinal);
        var anchor = "";
        var cursor = 0;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < lines.Length; index++)
        {
            if (regions.TryGetValue(index, out var region))
            {
                if (!keys.Add(region)) return null;
                var prefix = string.Join('\n', lines[cursor..index]);
                if (index > cursor) prefix += "\n";
                skeleton[anchor] = prefix;
                anchor = region;
                order.Add(region);
                cursor = index;
            }
            var match = strategy.RowPattern.Match(lines[index]);
            if (!match.Success || !entryStarts.Contains(offsets[index] + lines[index].Length - lines[index].TrimStart().Length)) continue;
            var start = index;
            while (start > cursor && lines[start - 1].TrimStart().StartsWith("//", StringComparison.Ordinal)) start--;
            var raw = string.Join('\n', lines[cursor..start]);
            if (start > cursor) raw += "\n";
            skeleton[anchor] = raw;
            var identity = match.Groups["identity"].Value;
            var key = strategy.Key(match);
            if (!keys.Add(key) || (match.Groups["ceiling"].Success && !int.TryParse(match.Groups["ceiling"].Value, out _))) return null;
            entries.Add(new(key, identity, lines[start..index], lines[index]));
            order.Add(key);
            anchor = key;
            cursor = index + 1;
        }
        skeleton[anchor] = string.Join('\n', lines[cursor..]);
        // If an actual constructor wasn't recognized as a whole row, the grammar isn't ours.
        if (entries.Count == 0 || entries.Count != entryStarts.Count) return null;
        return new(entries, order, skeleton);
    }

    internal static bool WritePlans(string root, IReadOnlyList<Plan> plans)
    {
        // First materialize the complete layout, so even a self-guarded registry can be measured.
        foreach (var plan in plans) Write(root, plan, Render(plan, _ => null, false)!);
        foreach (var plan in plans)
        {
            var rendered = Render(plan, relative => SourceSizeRatchet.Evaluate(root,
                [new SourceSizeCeiling(relative, -1)]).Single().ActualLineCount, true);
            if (rendered is null) return false;
            Write(root, plan, rendered);
        }
        return true;
    }

    private static void Write(string root, Plan plan, byte[] bytes) => File.WriteAllBytes(Path.Combine(root, plan.Path), bytes);

    private static byte[]? Render(Plan plan, Func<string, int?> measure, bool remeasure)
    {
        var entries = plan.Entries.ToDictionary(entry => entry.Key, StringComparer.Ordinal);
        var result = new StringBuilder();
        var failed = false;
        void AppendEntry(string key)
        {
            if (!entries.TryGetValue(key, out var entry)) return;
            var row = entry.Row;
            if (entry.Remeasure && remeasure)
            {
                var count = measure(entry.Identity);
                if (count is null) { failed = true; return; }
                var match = Registries[plan.Path].RowPattern.Match(row).Groups["ceiling"];
                row = row[..match.Index] + count.Value.ToString(CultureInfo.InvariantCulture) + row[(match.Index + match.Length)..];
            }
            foreach (var comment in entry.Comments) result.Append(comment).Append('\n');
            result.Append(row).Append('\n');
        }
        void AppendAdditions(string anchor)
        {
            if (plan.Additions.TryGetValue(anchor, out var keys)) foreach (var key in keys) AppendEntry(key);
        }
        result.Append(plan.Skeleton.GetValueOrDefault("", ""));
        AppendAdditions("");
        foreach (var key in plan.BaseOrder)
        {
            AppendEntry(key);
            AppendAdditions(key);
            result.Append(plan.Skeleton.GetValueOrDefault(key, ""));
        }
        return failed ? null : [.. plan.Bom ? new byte[] { 0xef, 0xbb, 0xbf } : Array.Empty<byte>(),
            .. Utf8.GetBytes(result.ToString().ReplaceLineEndings(plan.LineEnding))];
    }

    internal static bool HasCeilingBelowMeasured(string root)
    {
        var ceilings = SourceSizeRatchetPreflight.TryReadAuthority(root);
        return ceilings is null || SourceSizeRatchet.Evaluate(root, ceilings).Any(violation => violation.ActualLineCount is not null);
    }

    internal static int CountHunks(byte[] bytes) => Regex.Matches(Utf8.GetString(bytes), "^<{7,} ", RegexOptions.Multiline).Count;
}
