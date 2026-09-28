using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

/// <summary>A Developer retry that needs focused evidence on its unchanged candidate.</summary>
public sealed record DeferredNoChangeOutcome(
    string CandidateSha,
    IReadOnlyList<string> TestClasses,
    string Rationale)
{
    private const string Marker = "DEFERRED_NO_CHANGE_OUTCOME=";

    public static bool ContainsMarker(string? text) =>
        text?.Contains(Marker, StringComparison.Ordinal) == true;

    public string FormatMarker() => Marker + Convert.ToBase64String(
        JsonSerializer.SerializeToUtf8Bytes(this));

    public static bool TryParse(string? text, out DeferredNoChangeOutcome outcome)
    {
        outcome = default!;
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            var index = line.IndexOf(Marker, StringComparison.Ordinal);
            if (index < 0) continue;
            var encoded = line[(index + Marker.Length)..].Trim().Split(' ', 2)[0];
            try
            {
                var parsed = JsonSerializer.Deserialize<DeferredNoChangeOutcome>(
                    Convert.FromBase64String(encoded));
                if (parsed is not null &&
                    Regex.IsMatch(parsed.CandidateSha, "^[a-fA-F0-9]{40}(?:[a-fA-F0-9]{24})?$") &&
                    parsed.TestClasses is { Count: > 0 } &&
                    parsed.TestClasses.All(DeveloperDeferredTestClassNames.IsValid) &&
                    !string.IsNullOrWhiteSpace(parsed.Rationale))
                {
                    outcome = parsed;
                    return true;
                }
            }
            catch (Exception exception) when (exception is FormatException or JsonException)
            {
                // A malformed marker is never evidence.
            }
        }
        return false;
    }
}

public static class DeveloperDeferredTestClassNames
{
    private static readonly Regex ClassToken = new(
        @"^[A-Z][A-Za-z0-9_]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex QuotedClassToken = new(
        "(?<quote>[`\"'])(?<name>[A-Z][A-Za-z0-9_]*)\\k<quote>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LeadingConnective = new(
        @"^(?:naming|for|of)(?=$|[\s:\-\u2014\u2013])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex ClassListSeparator = new(
        @"\s*,\s*(?:and\s+)?|\s+and\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly char[] DeclarationSeparators = [' ', ':', '-', '\u2014', '\u2013'];

    public static bool IsValid(string? name) => name is not null && ClassToken.IsMatch(name);

    public static IReadOnlyList<string> Parse(string testsField)
    {
        var deferred = testsField.IndexOf("deferred", StringComparison.OrdinalIgnoreCase);
        if (deferred < 0) return [];
        var declaration = testsField[(deferred + "deferred".Length)..].TrimStart(DeclarationSeparators);
        declaration = declaration.Split([';', '\r', '\n'], 2)[0];
        var connective = LeadingConnective.Match(declaration);
        if (connective.Success)
            declaration = declaration[connective.Length..].TrimStart(DeclarationSeparators);
        var names = new List<string>();
        var barePrefix = true;
        foreach (var part in ClassListSeparator.Split(declaration).Select(value => value.Trim()))
        {
            var wrapped = QuotedClassToken.Matches(part);
            if (wrapped.Count > 0)
            {
                names.AddRange(wrapped.Select(match => match.Groups["name"].Value));
                continue;
            }
            if (barePrefix && IsValid(part)) names.Add(part);
            else barePrefix = false;
        }
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
