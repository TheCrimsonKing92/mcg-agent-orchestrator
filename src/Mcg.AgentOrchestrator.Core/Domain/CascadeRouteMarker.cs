using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

/// <summary>A dispatch route receipt embedded at the end of its selection reason.</summary>
public sealed record CascadeRouteMarker(string Decision, string RuleId, IReadOnlyList<string>? Ids = null)
{
    public const string Cheap = "cheap";
    public const string Escalated = "escalated";
    public const string Primary = "primary";
    private static readonly Regex Pattern = new(
        @"(?:\A|; )cascade=(cheap|escalated|primary) rule=([a-z0-9-]+)(?: ids=([^\s;,]+(?:,[^\s;,]+)*))?\z",
        RegexOptions.CultureInvariant);

    public string Format()
    {
        if (Decision is not (Cheap or Escalated or Primary) ||
            !Regex.IsMatch(RuleId, @"\A[a-z0-9-]+\z", RegexOptions.CultureInvariant) ||
            Ids?.Any(id => string.IsNullOrEmpty(id) || id.Any(c => char.IsWhiteSpace(c) || c is ',' or ';')) == true)
            throw new ArgumentException("Invalid cascade route marker.");
        return $"cascade={Decision} rule={RuleId}" +
            (Ids is { Count: > 0 } ? $" ids={string.Join(",", Ids)}" : string.Empty);
    }

    public string AppendTo(string? reason) => string.IsNullOrEmpty(reason) ? Format() : $"{reason}; {Format()}";

    public static bool TryParse(string? reason, out CascadeRouteMarker marker)
    {
        marker = null!;
        if (string.IsNullOrEmpty(reason)) return false;
        var match = Pattern.Match(reason);
        if (!match.Success) return false;
        marker = new(match.Groups[1].Value, match.Groups[2].Value,
            match.Groups[3].Success ? match.Groups[3].Value.Split(',') : []);
        return true;
    }
}
