using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

public sealed record FrozenFactAmendment(string Fact, string File, string AllowedChange);

/// <summary>The operator's bounded four-step ruling, shared by Author and later goal roles.</summary>
public sealed record FrozenFactRuling(
    IReadOnlyList<string> FrozenClasses,
    IReadOnlyList<FrozenFactAmendment> AmendedFacts,
    string Basis,
    string Unmodified,
    string DiffCheck,
    IReadOnlyList<string> EvidenceReferences)
{
    public const string Header = "Frozen-fact ruling v1";

    public string Render()
    {
        var lines = new List<string> { Header, "Frozen classes: " + Join(FrozenClasses) };
        lines.AddRange(AmendedFacts.Select(fact =>
            $"Amended fact: {OneLine(fact.Fact)} in {OneLine(fact.File)}: {OneLine(fact.AllowedChange)}"));
        lines.Add("Basis: " + OneLine(Basis));
        lines.Add("Unmodified: " + OneLine(Unmodified));
        lines.Add("Diff check: " + OneLine(DiffCheck));
        lines.Add("Evidence: " + Join(EvidenceReferences));
        return string.Join(Environment.NewLine, lines);
    }

    public static FrozenFactRuling? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var lines = text.ReplaceLineEndings("\n").Split('\n')
            .Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
        if (lines.Length == 0 || lines[0] != Header) return null;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var facts = new List<FrozenFactAmendment>();
        foreach (var line in lines.Skip(1))
        {
            if (line.StartsWith("Amended fact: ", StringComparison.Ordinal))
            {
                var value = line["Amended fact: ".Length..];
                var inIndex = value.IndexOf(" in ", StringComparison.Ordinal);
                if (inIndex <= 0) return null;
                var fileStart = inIndex + 4;
                var colonIndex = value.IndexOf(": ", fileStart, StringComparison.Ordinal);
                if (colonIndex <= fileStart) return null;
                facts.Add(new(value[..inIndex], value[fileStart..colonIndex], value[(colonIndex + 2)..]));
                continue;
            }
            var separator = line.IndexOf(':');
            if (separator < 0) return null;
            var label = line[..separator];
            if (label is not ("Frozen classes" or "Basis" or "Unmodified" or "Diff check" or "Evidence") ||
                !fields.TryAdd(label, line[(separator + 1)..].Trim())) return null;
        }
        if (fields.Count != 5 || facts.Count == 0) return null;
        return new(Split(fields["Frozen classes"]), facts, fields["Basis"],
            fields["Unmodified"], fields["Diff check"], Split(fields["Evidence"]));
    }

    private static string[] Split(string value) => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    private static string Join(IEnumerable<string> values) => string.Join(", ", values.Select(OneLine));
    private static string OneLine(string value) => Regex.Replace(value.Trim(), @"\s+", " ");
}
